import { chromium } from 'playwright';
import { postPumpEvent, postStructuredPump, postPumpTrade, heartbeat } from './client';
import { transformItem, type PumpFeedItem } from './transform';
import { runVerifiedSync } from './verifiedSync';
import { logTiming } from './timingLog';
import { startAlertStream, type AlertStream, type StreamEvent } from './stream';
import { startNatsMirror, type NatsMirror } from './natsMirror';
import { logPumpTrade, tradeFromEvent, tradeFromItem, type PumpTradeRecord } from './tradeLog';
import { existsSync, readFileSync, unlinkSync, writeFileSync } from 'fs';
import { join } from 'path';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
const PROFILE_DIR = './chromium-profile';
// The page the sidecar sits on. Any pump.fun page carries the login, the app's NATS worker and the alerts subscription, so it sits on
// the not-found page: the home page also renders live coin lists, which subscribe it to the trades of dozens of coins (thousands of
// frames a minute for nothing) and grew the tab to 2.5 GB, against ~0.6 GB here (measured 2026-10-29). PUMP_PAGE_URL overrides it.
let pumpUrl = process.env.PUMP_PAGE_URL || 'https://pump.fun/404';
const ALERTS_API = 'https://frontend-api-v3.pump.fun/following-positions/alerts?pageSize=50&kinds=callout,repost,reply,trade&minTradeAmountUsd=10';
// A trade older than this is recorded but never alerted (restart / outage backlog must not replay as alerts).
const MAX_TRADE_ALERT_AGE_MS = 90_000;
// Same floor the feed query uses (minTradeAmountUsd=10), so the live stream alerts on exactly what polling did.
const MIN_TRADE_ALERT_USD = 10;
const HEARTBEAT_INTERVAL_MS = 30_000;
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));
// The page does not need a single picture, video or font (it is only listened to), and the coin images of its live feed filled a tab to
// ~2 GB within an hour. They are refused before they download. Network sockets (the live NATS feed) are not touched. PUMP_LEAN_PAGE=0 turns it off.
const LEAN_PAGE = process.env.PUMP_LEAN_PAGE !== '0';
const lean = async (p: import('playwright').Page): Promise<void> => {
  if (!LEAN_PAGE) return;
  await p.route('**/*', (route) => {
    const t = route.request().resourceType();
    if (t === 'image' || t === 'media' || t === 'font') void route.abort(); else void route.continue();
  });
};
// pump.fun's own contract for GET /following-positions/alerts: the first page is memoized for
// `maxAgeSeconds` ("Never poll faster than this"), and live news comes over NATS (see stream.ts).
// Hammering it at 2 req/s got this IP a Cloudflare challenge, so polling is now a safety net:
// slow while the live stream is healthy, a little quicker (never faster than maxAge) while it is not.
const STREAM_FALLBACK_POLL_MS = Number(process.env.STREAM_FALLBACK_POLL_MS) || 30_000;
const DEGRADED_POLL_MS = Number(process.env.POLL_INTERVAL_MS) || 2_000;   // the server memoizes the first page for 2s: no point asking faster
const MIN_POLL_GAP_MS = 400;          // never two fetches closer than this, whatever rings the doorbell
const RATE_LIMIT_BACKOFF_MS = 30_000;
const FETCH_FAIL_BACKOFF_MS = 5_000;
// Remembered externalIds, so an item already handled is not re-processed by a later fetch.
const CLAIM_MEMORY = 500;
const PROBE_INTERVAL_MS = 15_000;
const PROBE_TIMEOUT_MS = 10_000;
const PROBE_FAILURES_BEFORE_RELOAD = 3;
// A page left open for days keeps growing (its renderer process reached 2 GB in 9 hours). Reloading it in place does not give that memory
// back, so the page is ROTATED instead: a second page opens and starts receiving the live stream while the first keeps working, and only
// when the new one is really receiving does the old one close (its process, and the memory in it, goes away). Every event is deduplicated
// by id downstream (seenTrades / claimed / the backend), so both pages delivering the same event is harmless. The browser, its cookies
// and the Cloudflare clearance stay as they are.
const PAGE_REFRESH_MS = (Number(process.env.PAGE_REFRESH_HOURS) || 8) * 60 * 60_000;
const VERIFIED_SYNC_INTERVAL_MS = Number(process.env.VERIFIED_SYNC_INTERVAL_MS) || 6 * 60 * 60_000; // 6h default
// Extra connections from Node to the alerts stream next to the page's own (see natsMirror.ts). PUMP_NATS_MIRROR=0 turns them off.
const MIRROR_CONNECTIONS = process.env.PUMP_NATS_MIRROR === '0' ? 0 : Number(process.env.PUMP_NATS_MIRROR) || 2;

interface AlertsProbeResult {
  ok: boolean;
  status?: number;
  data?: { items?: PumpFeedItem[]; maxAgeSeconds?: number };
}

// Login check that does not touch the alerts endpoint (so waiting for a login never adds to its load).
async function isLoggedIn(page: import('playwright').Page): Promise<boolean> {
  return page.evaluate(async () => {
    try {
      const res = await fetch('https://frontend-api-v3.pump.fun/auth/my-profile', { credentials: 'include' });
      return res.ok;
    } catch { return false; }
  }).catch(() => false);
}

async function fetchAlerts(page: import('playwright').Page, apiUrl: string = ALERTS_API): Promise<AlertsProbeResult> {
  const result = await page.evaluate(async (url) => {
    const res = await fetch(url, { credentials: 'include' });
    if (!res.ok) return { ok: false, status: res.status };
    const data = await res.json();
    return { ok: true, data };
  }, apiUrl);
  return result as AlertsProbeResult;
}

async function session(firstRun: boolean): Promise<void> {
  console.log(`${ts()} [main] launching browser (headless=%s)`, HEADLESS);

  const context = await chromium.launchPersistentContext(PROFILE_DIR, {
    channel: 'chrome',
    headless: HEADLESS,
    args: [
      '--disable-blink-features=AutomationControlled',
      // This box is a Hyper-V VM with no real GPU (only the virtual Hyper-V/RDP
      // adapters) — letting Chrome try to use one crashes the GPU process, which
      // cascades into renderer crashes and an endless recover()-reload loop.
      '--disable-gpu',
      '--disable-software-rasterizer',
      '--disable-gpu-compositing',
      // The window may be hidden or behind a closed lid: keep Chrome from throttling
      // or "occluding" the page, or the live NATS feed would go quiet.
      '--disable-background-timer-throttling',
      '--disable-renderer-backgrounding',
      '--disable-backgrounding-occluded-windows',
    ],
    ignoreDefaultArgs: ['--enable-automation'],
  });

  // pump.fun pauses its live feed when the page thinks it is hidden, and its NATS client lives in a Web
  // Worker: keep a handle on that worker (stream.ts talks to it) and make the page always look visible.
  // (browser-side code as a string: this project's tsconfig has no DOM lib)
  await context.addInitScript({ content: `
    try {
      Object.defineProperty(document, 'hidden', { get: () => false });
      Object.defineProperty(document, 'visibilityState', { get: () => 'visible' });
      document.addEventListener('visibilitychange', (e) => e.stopImmediatePropagation(), true);
    } catch (e) { /* best effort */ }
    (() => {
      const Orig = window.Worker;
      window.__workers = [];
      window.__wlog = [];
      const note = (o) => { try { if (window.__wlog.length < 400) window.__wlog.push(o); } catch (e) {} };
      // What the page and its NATS worker say to each other, as TYPES only (instance / status / reason): never
      // credentials, tokens or payloads. Drained by stream.ts so a silent stream has a reason in the log.
      const mask = (s) => (typeof s === 'string' ? s.split('.').map((p, i) => (i === 0 ? p : p.length > 8 ? p.slice(0, 3) + '~' : p)).join('.') : undefined);
      window.Worker = new Proxy(Orig, { construct(target, args) {
        const w = new target(...args);
        window.__workers.push(w);
        try {
          const post = w.postMessage.bind(w);
          w.__rawPost = post;   // stream.ts posts through this one so its own messages are not mistaken for the page's
          w.postMessage = (m, ...rest) => {
            try {
              if (m && typeof m === 'object') {
                // what natsMirror.ts needs to connect like the worker does: the CORE server + the site's public subscriber
                // login, and the per-user token once pump.fun turns it on. Kept in the page, read only by natsMirror.ts.
                if (m.type === 'configs' && m.configs && m.configs.CORE) window.__natsCore = m.configs.CORE;
                if (m.type === 'auth' && m.instance === 'CORE') window.__natsJwt = (m.auth && m.auth.jwt) || null;
                if (m.type === 'subscribe' && typeof m.channel === 'string' && m.channel.indexOf('alertsFeed.user.') === 0) window.__pageAlertsSub = true;
                const o = { dir: 'out', type: m.type, instance: m.instance };
                if (m.type === 'configs' && m.configs) o.configInstances = Object.keys(m.configs).join(',');
                if (m.type === 'auth') o.hasAuth = !!m.auth;
                if (m.type === 'subscribe' || m.type === 'unsubscribe') o.channel = mask(m.channel);
                note(o);
              }
            } catch (e) {}
            return post(m, ...rest);
          };
          w.addEventListener('message', (ev) => {
            try {
              const d = ev.data;
              if (d && typeof d === 'object' && d.type && !(d.channel && d.data !== undefined)) note({ dir: 'in', type: d.type, instance: d.instance, status: d.status || d.state, reason: d.reason, code: d.code });
            } catch (e) {}
          });
        } catch (e) {}
        return w;
      } });
    })();
  ` });

  let hbInterval: NodeJS.Timeout | undefined;
  let probeInterval: NodeJS.Timeout | undefined;
  let pollInterval: NodeJS.Timeout | undefined;
  let verifiedSyncInterval: NodeJS.Timeout | undefined;
  let followUpTimer: NodeJS.Timeout | undefined;
  let refreshTimer: NodeJS.Timeout | undefined;
  let triggerInterval: NodeJS.Timeout | undefined;
  let stream: AlertStream | undefined;
  let mirror: NatsMirror | undefined;

  try {
    let page = context.pages()[0] ?? await context.newPage();
    // Events seen by each page while two pages overlap during a rotation (null the rest of the time): the evidence that nothing is lost.
    let overlap: { oldPage: import('playwright').Page; old: Set<string>; neu: Set<string> } | null = null;
    const seenBy = (p: import('playwright').Page, id: string) => { const o = overlap; if (o) (p === o.oldPage ? o.old : o.neu).add(id); };

    let endSession!: (reason: string) => void;
    const sessionEnded = new Promise<string>((resolve) => { endSession = resolve; });

    // Listen for pump.fun's live feed from the very start: its NATS websocket opens at page load,
    // before the login check below finishes. Presence registration waits for the login (stream.begin()).
    let ring: (ev: StreamEvent) => void = () => { /* wired once the poller exists */ };
    // The same event can arrive from the page and from each mirror connection (and from both pages during a rotation): only the
    // first copy rings, so a duplicate never costs an extra fetch of the alerts API.
    const heard = new Set<string>();
    let heardTotal = 0, heardFirstByMirror = 0;
    const hear = (ev: StreamEvent, via: 'page' | 'mirror') => {
      const key = `${ev.kind}:${ev.id}`;
      if (heard.has(key)) return;
      heard.add(key);
      if (heard.size > 5_000) heard.delete(heard.values().next().value as string);
      heardTotal++;
      if (via === 'mirror') heardFirstByMirror++;
      ring(ev);
    };
    const firstPage = page;
    stream = startAlertStream(page, (ev) => { seenBy(firstPage, ev.id); hear(ev, 'page'); }, (m) => console.log(`${ts()} ${m}`));

    const emulateFocus = async (p: import('playwright').Page) => {
      try {
        const cdp = await context.newCDPSession(p);
        await cdp.send('Emulation.setFocusEmulationEnabled', { enabled: true });   // page stops its live feed when it thinks it is unfocused
      } catch (err) { console.warn(`${ts()} [main] focus emulation failed:`, String(err).split('\n')[0]); }
    };
    await emulateFocus(page);
    await lean(page);

    console.log(`${ts()} [main] navigating to`, pumpUrl);
    await page.goto(pumpUrl, { waitUntil: 'domcontentloaded' });

    if (firstRun) {
      // First run: no session yet. Give the user time to log into pump.fun by hand
      // in this (visible, HEADLESS=false) window — persists into PROFILE_DIR after that.
      console.log(`${ts()} [main] waiting for pump.fun login (up to 5 min)...`);
      let loggedIn = false;
      const deadline = Date.now() + 300_000;
      while (Date.now() < deadline) {
        if (await isLoggedIn(page)) { loggedIn = true; break; }
        await new Promise((r) => setTimeout(r, 5_000));
      }
      if (!loggedIn) {
        console.error(`${ts()} [main] ❌ Login timed out after 5 minutes, exiting.`);
        process.exit(1);
      }
      console.log(`${ts()} [main] ✅ logged in, session persisted to ${PROFILE_DIR}`);
    }
    stream.begin();
    if (MIRROR_CONNECTIONS > 0) {
      mirror = startNatsMirror({
        page: () => page, subject: () => stream?.subject() ?? null, pageHearing: () => !!stream?.healthy(),
        onEvent: (ev) => hear(ev, 'mirror'), log: (m) => console.log(`${ts()} ${m}`), connections: MIRROR_CONNECTIONS,
      });
    }

    let recovering = false;
    const recover = async (reason: string) => {
      if (recovering) return;
      recovering = true;
      console.warn(`${ts()} [main] recovering page (${reason})...`);
      try {
        await page.goto(pumpUrl, { waitUntil: 'domcontentloaded' });
        console.log(`${ts()} [main] ✅ page recovered`);
        logTiming('recovered');
      } catch (err) {
        console.error(`${ts()} [main] in-place recovery failed, relaunching browser:`, err);
        endSession(`in-place recovery failed after: ${reason}`);
      } finally {
        recovering = false;
      }
    };

    // Only the CURRENT page matters: the old one is closed on purpose during a rotation, and a page that never became current is ignored.
    const watch = (p: import('playwright').Page) => {
      p.on('crash', () => { if (p === page) void recover('page crashed (crash event)'); });
      p.on('close', () => { if (p === page) endSession('page closed'); });
    };
    watch(page);
    context.on('close', () => endSession('browser closed'));

    // One fetch at a time. pump.fun tells us when there is news (the live stream "rings" a doorbell,
    // see stream.ts and ring() below) and a slow timer is the safety net. Processing is fired and
    // forgotten so a slow POST never stalls polling, and items are claimed by externalId so no item is
    // ever handled twice, however a fetch was triggered.
    let inFlight = 0;
    let pollAgain = false;
    let lastPollAt = 0;
    let lastMaxAgeS = 0;
    let lastTickLog = 0;
    let tickCount = 0;
    let backoffUntil = 0;
    let failStreak = 0;
    let tradesSeen = 0;
    let tradeEventsFull = 0;     // live trade events that carried everything
    let tradeEventsStub = 0;     // live trade events that were only a doorbell (coin without details yet)
    let stubFollowUntil = 0;     // while now < this, the feed is polled quickly because a stub just rang
    const claimed = new Set<string>();

    const claim = (id: string): boolean => {
      if (claimed.has(id)) return false;
      claimed.add(id);
      // Set iterates in insertion order, so this evicts oldest-first.
      while (claimed.size > CLAIM_MEMORY) {
        const oldest = claimed.values().next().value as string | undefined;
        if (oldest === undefined) break;
        claimed.delete(oldest);
      }
      return true;
    };

    const pollOnce = async (reason: string): Promise<void> => {
      if (recovering || Date.now() < backoffUntil) return;
      if (inFlight > 0 || Date.now() - lastPollAt < MIN_POLL_GAP_MS) { pollAgain = true; return; }   // the 1s tick picks it up
      inFlight++;
      tickCount++;
      lastPollAt = Date.now();
      pollAgain = false;
      await (async () => {
        try {
          const t0 = Date.now();
          const probe = await fetchAlerts(page);
          const fetchMs = Date.now() - t0;
          lastMaxAgeS = probe.data?.maxAgeSeconds ?? lastMaxAgeS;
          if (Date.now() - lastTickLog > 10_000 || reason.startsWith('ring')) {
            lastTickLog = Date.now();
            console.log(`${ts()} [poll] fetch=${fetchMs}ms reason=${reason} polls=${tickCount} trades=${tradesSeen} stream-trades full=${tradeEventsFull} stub=${tradeEventsStub} | ${stream?.describe()} | ${mirror?.describe() ?? 'mirror off'} heard=${heardTotal} first-by-mirror=${heardFirstByMirror}`);
            logTiming('poll', reason, `fetchMs=${fetchMs}`, `polls=${tickCount}`, `streamHealthy=${stream?.healthy()}`);
            tickCount = 0;
          }
          if (!probe.ok) {
            // At 1/s a failing endpoint would be hammered 60x/min. Back off instead —
            // longer for 429 (we are being told to slow down) than for a transient error
            // or an expired session, which the health check recovers separately.
            failStreak++;
            const wait = Math.min(300_000, (probe.status === 429 ? RATE_LIMIT_BACKOFF_MS : FETCH_FAIL_BACKOFF_MS) * 2 ** Math.min(failStreak - 1, 6));
            backoffUntil = Date.now() + wait;
            console.warn(`${ts()} [poll] alerts fetch failed (status=${probe.status ?? 'n/a'}) — backing off ${wait / 1000}s`);
            logTiming('fail', `status=${probe.status ?? 'n/a'}`, `fetchMs=${fetchMs}`, `backoffMs=${wait}`);
            return;
          }

          failStreak = 0;
          for (const item of probe.data?.items ?? []) {
            if (item.kind === 'trade') { handleTrade(item, t0 + fetchMs); continue; }
            const transformed = transformItem(item);
            if (!transformed) continue; // update/quote — not ingested (see HANDOFF.md)
            const id = transformed.externalId;
            if (!claim(id)) continue;   // already handled or in flight from an earlier tick

            // seenLagMs: pump.fun's createdAt -> the fetch that first showed it to us returning
            // (our round trip counts). handoffMs: our POSTs to the backend.
            const seenLagMs = t0 + fetchMs - Date.parse(transformed.structured.createdAt);
            void (async () => {
              try {
                const p0 = Date.now();
                const accepted = await postPumpEvent({ ...item, externalId: id });
                if (!accepted) return;  // duplicate — backend already processed this one
                await postStructuredPump(transformed.structured);
                logTiming('item', transformed.structured.kind, id, transformed.structured.symbol,
                  transformed.structured.createdAt, `seenLagMs=${seenLagMs}`, `fetchMs=${fetchMs}`, `handoffMs=${Date.now() - p0}`);
              } catch (err) {
                claimed.delete(id);     // release so a later tick can retry it
                console.error(`${ts()} [poll] process error for externalId=${id}:`, err);
              }
            })();
          }
        } catch (err) {
          // A throwing fetch ("Failed to fetch", e.g. a Cloudflare challenge) used to retry every tick,
          // hammering the API at full rate. Back off exponentially instead (5s, 10s, ... max 5 min).
          failStreak++;
          backoffUntil = Date.now() + Math.min(300_000, FETCH_FAIL_BACKOFF_MS * 2 ** Math.min(failStreak - 1, 6));
          console.error(`${ts()} [poll] error:`, err);
        } finally {
          inFlight--;
        }
      })();
    };

    // The doorbell. For callouts / replies / reposts the live event only says "a new row exists" (pump.fun's
    // own page then re-fetches the feed), so do exactly that: one fetch now, and one more once the server's
    // memoized first page (maxAgeSeconds) can have changed.
    ring = (ev: StreamEvent) => {
      if (ev.kind === 'trade') {
        // Self-contained: the site itself inserts a trade straight from the event, so no fetch is needed and
        // the alert can leave within a few hundred ms of the trade.
        const rec = tradeFromEvent(ev.raw, ev.receivedAtMs);
        if (rec) {
          tradeEventsFull++;
          const e = ev.raw as { coin?: { mint?: string; chainId?: number; symbol?: string; imageUri?: string }; author?: unknown; trade?: unknown; createdAt?: string };
          // same shape as the feed rows the backend already stores (coinMint, chainId, symbol, coinImage at the top)
          acceptTrade(rec, { kind: 'trade', author: e.author, coinMint: e.coin?.mint, chainId: e.coin?.chainId, symbol: e.coin?.symbol, coinImage: e.coin?.imageUri, createdAt: e.createdAt, trade: e.trade });
        } else if ((ev.raw as { stub?: unknown } | null)?.stub) {
          // A "stub" trade event is only a doorbell (the coin has no details yet, so the event carries none): pump.fun's
          // own page reacts by re-fetching the feed. Do the same now, and keep asking every ~1.5 s for 10 s, because the
          // feed row shows up a few seconds later. Before this, such trades waited for the 10 s safety poll plus that lag.
          tradeEventsStub++;
          stubFollowUntil = Date.now() + 10_000;
          void pollOnce('ring:stub');
        }
        return;
      }
      const lagMs = ev.createdAt ? ev.receivedAtMs - Date.parse(ev.createdAt) : NaN;
      logTiming('ring', ev.kind, ev.id, `serverToRingMs=${Number.isFinite(lagMs) ? lagMs : ''}`);
      void pollOnce(`ring:${ev.kind}`);
      if (followUpTimer) clearTimeout(followUpTimer);
      followUpTimer = setTimeout(() => { void pollOnce('ring-followup'); }, Math.max(1_200, lastMaxAgeS * 1_000 + 300));
    };

    // Followed traders' buys/sells ride the same feed ("kinds" includes trade): every one is recorded to
    // ./logs, and one that just happened is handed to the backend, which applies the rollout switch
    // (PumpTradeAlerts) and the users' settings. Older ones (first fetch after a restart, an outage) are
    // recorded only, so a restart can never replay history as alerts.
    const seenTrades = new Set<string>();
    let tradeSampleSaved = false;
    const handleTrade = (item: PumpFeedItem, fetchedAt: number): void => {
      const rec = tradeFromItem(item, fetchedAt);
      if (rec) acceptTrade(rec, item);
    };
    // One trade, whichever way it arrived (live stream event or feed poll): the first to bring a tx wins.
    const acceptTrade = (rec: PumpTradeRecord, raw: unknown): void => {
      const item = raw;
      const key = rec.tx ?? rec.eventId;
      if (!key || seenTrades.has(key)) return;
      seenTrades.add(key);
      if (seenTrades.size > 5_000) seenTrades.delete(seenTrades.values().next().value as string);
      if (!tradeSampleSaved) {
        tradeSampleSaved = true;   // the real row shape, in case the parser above guessed wrong
        try { writeFileSync(join(__dirname, '..', 'logs', 'trade-lane-sample.json'), JSON.stringify({ at: new Date().toISOString(), item }, null, 1)); } catch { /* best effort */ }
      }
      logPumpTrade(rec);
      tradesSeen++;
      const fresh = rec.lagMs !== null && rec.lagMs <= MAX_TRADE_ALERT_AGE_MS;
      if (!fresh || !rec.tx || !rec.trader || !rec.mint || rec.chainId === null || !rec.symbol || rec.isBuy === null || !(rec.amountUsd !== null && rec.amountUsd >= MIN_TRADE_ALERT_USD)) return;
      const tx = rec.tx;
      void (async () => {
        try {
          const p0 = Date.now();
          const accepted = await postPumpEvent({ ...(item as Record<string, unknown>), externalId: tx });
          if (!accepted) return;   // already handled (duplicate)
          await postPumpTrade({
            externalId: tx, actorHandle: rec.trader!, actorUserId: rec.traderUserId, coinMint: rec.mint!, chainId: rec.chainId!,
            symbol: rec.symbol!, marketCap: rec.marketCapUsd, isBuy: rec.isBuy!, amountUsd: rec.amountUsd!, createdAt: rec.createdAt ?? rec.seenAt,
            recvAtMs: Date.parse(rec.seenAt), via: rec.source,
          });
          logTiming('trade', tx, rec.symbol, rec.createdAt, `via=${rec.source}`, `seenLagMs=${rec.lagMs}`, `handoffMs=${Date.now() - p0}`);
        } catch (err) {
          console.error(`${ts()} [poll] trade hand-off error for tx=${tx}:`, err);
        }
      })();
    };

    // Safety net: slow while the live stream is healthy, otherwise as quick as the site's own contract
    // allows (never faster than maxAgeSeconds). Also drains a poll that had to wait for the minimum gap.
    pollInterval = setInterval(() => {
      const every = Date.now() < stubFollowUntil ? 1_500
        : stream?.healthy() ? STREAM_FALLBACK_POLL_MS : Math.max(DEGRADED_POLL_MS, lastMaxAgeS * 1_000);
      if (pollAgain || Date.now() - lastPollAt >= every) void pollOnce(stream?.healthy() ? 'fallback' : 'degraded');
    }, 1_000);

    let probeFailures = 0;
    let probing = false;
    probeInterval = setInterval(() => {
      if (recovering || probing) return;
      probing = true;
      void (async () => {
        try {
          await Promise.race([
            page.evaluate('1'),
            new Promise((_, reject) => setTimeout(() => reject(new Error('probe timed out')), PROBE_TIMEOUT_MS)),
          ]);
          probeFailures = 0;
        } catch (err) {
          const msg = String(err);
          if (/crash/i.test(msg)) { void recover('renderer crashed (health check)'); return; }
          if (/context or browser has been closed/i.test(msg)) { endSession('browser closed (probe)'); return; }
          probeFailures++;
          console.warn(`${ts()} [main] health check failed (${probeFailures}/${PROBE_FAILURES_BEFORE_RELOAD}): ${msg}`);
          if (probeFailures >= PROBE_FAILURES_BEFORE_RELOAD) {
            probeFailures = 0;
            void recover('page unresponsive');
          }
        } finally {
          probing = false;
        }
      })();
    }, PROBE_INTERVAL_MS);

    hbInterval = setInterval(() => { heartbeat().catch(() => {}); }, HEARTBEAT_INTERVAL_MS);

    let rotating = false;
    const rotatePage = async (why: string, url: string = pumpUrl): Promise<boolean> => {
      if (rotating || recovering) return false;
      rotating = true;
      const old = page, oldStream = stream;
      let fresh: import('playwright').Page | undefined;
      let freshStream: AlertStream | undefined;
      try {
        console.log(`${ts()} [rotate] start (${why}): a second page opens on ${url} while the first keeps working`);
        overlap = { oldPage: old, old: new Set(), neu: new Set() };
        fresh = await context.newPage();
        await emulateFocus(fresh);
        await lean(fresh);
        watch(fresh);
        const freshPage = fresh;
        freshStream = startAlertStream(fresh, (ev) => { seenBy(freshPage, ev.id); hear(ev, 'page'); }, (m) => console.log(`${ts()} [new] ${m}`));
        await fresh.goto(url, { waitUntil: 'domcontentloaded' });
        freshStream.begin();
        const readyBy = Date.now() + 90_000;
        while (Date.now() < readyBy && !freshStream.ready()) await sleep(1_000);
        if (!freshStream.ready()) throw new Error('the new page was not receiving the live stream after 90 s, the old page stays');
        console.log(`${ts()} [rotate] new page is connected, now it has to prove it hears the same events as the old one`);
        // Connected is not enough: a second page can be subscribed and still hear nothing (seen in testing). From here on both pages are
        // compared event by event, and the old page is only closed once the new one has heard (nearly) everything the old one heard.
        overlap.old.clear(); overlap.neu.clear();
        const proveBy = Date.now() + 120_000, proveFrom = Date.now();
        const caughtUp = () => {
          const c = overlap!, missed = [...c.old].filter((id) => !c.neu.has(id)).length;
          return c.neu.size >= 1 && Date.now() - proveFrom >= 20_000 && missed <= Math.max(1, Math.floor(c.old.size * 0.25));
        };
        while (Date.now() < proveBy && !caughtUp()) await sleep(2_000);
        if (!freshStream.ready()) throw new Error('the new page stopped receiving during the overlap, the old page stays');
        if (!caughtUp()) throw new Error(`the new page did not hear what the old one heard (old ${overlap.old.size}, new ${overlap.neu.size} events), the old page stays`);
        await sleep(5_000);                        // a few more seconds of both at once
        const o = overlap, onlyOld = [...o.old].filter((id) => !o.neu.has(id)).length, onlyNew = [...o.neu].filter((id) => !o.old.has(id)).length;
        page = fresh; stream = freshStream;        // from here on, the new page is the one that counts
        pumpUrl = url;                             // and its address is the one later rotations and recoveries use
        oldStream?.stop();
        await old.close().catch(() => { /* already gone */ });
        console.log(`${ts()} [rotate] done: old page closed. During the overlap the old page saw ${o.old.size} events, the new ${o.neu.size}, only-old ${onlyOld}, only-new ${onlyNew}`);
        logTiming('rotate', why, `old=${o.old.size}`, `new=${o.neu.size}`, `onlyOld=${onlyOld}`, `onlyNew=${onlyNew}`);
        return true;
      } catch (err) {
        console.warn(`${ts()} [rotate] given up, nothing lost: ${String(err).slice(0, 200)}`);
        freshStream?.stop();
        if (fresh && page !== fresh) await fresh.close().catch(() => { /* already gone */ });
        return false;
      } finally {
        overlap = null;
        rotating = false;
      }
    };

    // Never on an exact clock: each rotation comes 80-120% of the interval after the last one. A failed one is retried in 10 minutes.
    const scheduleRotation = (ms: number) => {
      refreshTimer = setTimeout(async () => {
        const ok = await rotatePage('scheduled');
        scheduleRotation(ok ? PAGE_REFRESH_MS * (0.8 + Math.random() * 0.4) : 10 * 60_000);
      }, ms);
    };
    scheduleRotation(PAGE_REFRESH_MS * (0.8 + Math.random() * 0.4));

    // Manual trigger for tests and emergencies: create the file logs/rotate-now and the next check (5 s) rotates once. If the file
    // holds a pump.fun address, the new page opens there, and the sidecar stays on it only if it passes the same checks as any rotation.
    const triggerFile = join(__dirname, '..', 'logs', 'rotate-now');
    triggerInterval = setInterval(() => {
      if (!existsSync(triggerFile)) return;
      let wanted = '';
      try { wanted = readFileSync(triggerFile, 'utf8').trim(); } catch { /* unreadable: plain rotation */ }
      try { unlinkSync(triggerFile); } catch { /* gone already */ }
      void rotatePage('requested by file', /^https:\/\/pump\.fun\//.test(wanted) ? wanted : pumpUrl);
    }, 5_000);

    let syncingVerified = false;
    const runVerifiedSyncSafely = () => {
      if (recovering || syncingVerified) return;
      syncingVerified = true;
      void runVerifiedSync(page)
        .catch((err) => console.error(`${ts()} [verified-sync] error:`, err))
        .finally(() => { syncingVerified = false; });
    };
    // Disabled 2026-09-03 at rr3332's request — was auto-following every pump.fun
    // verified trader on the sidecar's own pump.fun account. Left in place, not
    // deleted, in case it needs to come back.
    // setTimeout(runVerifiedSyncSafely, 15_000);
    // verifiedSyncInterval = setInterval(runVerifiedSyncSafely, VERIFIED_SYNC_INTERVAL_MS);

    console.log(`${ts()} [main] Sidecar running — live pump.fun stream + ${STREAM_FALLBACK_POLL_MS / 1000}s fallback poll (${DEGRADED_POLL_MS / 1000}s while the stream is down), verified-trader sync disabled`);
    logTiming('start');

    const reason = await sessionEnded;
    console.warn(`${ts()} [main] session ended: ${reason}`);
  } finally {
    if (hbInterval) clearInterval(hbInterval);
    if (refreshTimer) clearTimeout(refreshTimer);
    if (triggerInterval) clearInterval(triggerInterval);
    if (probeInterval) clearInterval(probeInterval);
    if (pollInterval) clearInterval(pollInterval);
    if (followUpTimer) clearTimeout(followUpTimer);
    stream?.stop();
    mirror?.stop();
    if (verifiedSyncInterval) clearInterval(verifiedSyncInterval);
    await context.close().catch(() => {});
  }
}

async function main(): Promise<void> {
  let firstRun = true;
  for (;;) {
    try {
      await session(firstRun);
      firstRun = false;
    } catch (err) {
      console.error(`${ts()} [main] session error:`, err);
    }
    console.log(`${ts()} [main] relaunching browser in 5s...`);
    logTiming('relaunch');
    await new Promise((resolve) => setTimeout(resolve, 5_000));
  }
}

main().catch((err) => {
  console.error(`${ts()} [main] Fatal error:`, err);
  process.exit(1);
});
