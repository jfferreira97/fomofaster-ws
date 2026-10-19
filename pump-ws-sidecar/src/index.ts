import { chromium } from 'playwright';
import { postPumpEvent, postStructuredPump, heartbeat } from './client';
import { transformItem, type PumpFeedItem } from './transform';
import { runVerifiedSync } from './verifiedSync';
import { logTiming } from './timingLog';
import { startAlertStream, type AlertStream, type StreamEvent } from './stream';
import { logPumpTrade, tradeFromEvent } from './tradeLog';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
const PROFILE_DIR = './chromium-profile';
const PUMP_URL = 'https://pump.fun/';
const ALERTS_API = 'https://frontend-api-v3.pump.fun/following-positions/alerts?pageSize=20&kinds=callout,repost,reply&minTradeAmountUsd=10';
const HEARTBEAT_INTERVAL_MS = 30_000;
// pump.fun's own contract for GET /following-positions/alerts: the first page is memoized for
// `maxAgeSeconds` ("Never poll faster than this"), and live news comes over NATS (see stream.ts).
// Hammering it at 2 req/s got this IP a Cloudflare challenge, so polling is now a safety net:
// slow while the live stream is healthy, a little quicker (never faster than maxAge) while it is not.
const STREAM_FALLBACK_POLL_MS = Number(process.env.STREAM_FALLBACK_POLL_MS) || 30_000;
const DEGRADED_POLL_MS = Number(process.env.POLL_INTERVAL_MS) || 3_000;
const MIN_POLL_GAP_MS = 400;          // never two fetches closer than this, whatever rings the doorbell
const RATE_LIMIT_BACKOFF_MS = 30_000;
const FETCH_FAIL_BACKOFF_MS = 5_000;
// Remembered externalIds, so an item already handled is not re-processed by a later fetch.
const CLAIM_MEMORY = 500;
const PROBE_INTERVAL_MS = 15_000;
const PROBE_TIMEOUT_MS = 10_000;
const PROBE_FAILURES_BEFORE_RELOAD = 3;
const VERIFIED_SYNC_INTERVAL_MS = Number(process.env.VERIFIED_SYNC_INTERVAL_MS) || 6 * 60 * 60_000; // 6h default

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

async function fetchAlerts(page: import('playwright').Page): Promise<AlertsProbeResult> {
  const result = await page.evaluate(async (url) => {
    const res = await fetch(url, { credentials: 'include' });
    if (!res.ok) return { ok: false, status: res.status };
    const data = await res.json();
    return { ok: true, data };
  }, ALERTS_API);
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
      // The window lives on another virtual desktop / behind a closed lid: keep Chrome from throttling
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
      window.Worker = new Proxy(Orig, { construct(target, args) { const w = new target(...args); window.__workers.push(w); return w; } });
    })();
  ` });

  let hbInterval: NodeJS.Timeout | undefined;
  let probeInterval: NodeJS.Timeout | undefined;
  let pollInterval: NodeJS.Timeout | undefined;
  let verifiedSyncInterval: NodeJS.Timeout | undefined;
  let followUpTimer: NodeJS.Timeout | undefined;
  let stream: AlertStream | undefined;

  try {
    const page = context.pages()[0] ?? await context.newPage();

    let endSession!: (reason: string) => void;
    const sessionEnded = new Promise<string>((resolve) => { endSession = resolve; });

    // Listen for pump.fun's live feed from the very start: its NATS websocket opens at page load,
    // before the login check below finishes. Presence registration waits for the login (stream.begin()).
    let ring: (ev: StreamEvent) => void = () => { /* wired once the poller exists */ };
    stream = startAlertStream(page, (ev) => ring(ev), (m) => console.log(`${ts()} ${m}`));

    try {
      const cdp = await context.newCDPSession(page);
      await cdp.send('Emulation.setFocusEmulationEnabled', { enabled: true });   // page stops its live feed when it thinks it is unfocused
    } catch (err) { console.warn(`${ts()} [main] focus emulation failed:`, String(err).split('\n')[0]); }

    console.log(`${ts()} [main] navigating to`, PUMP_URL);
    await page.goto(PUMP_URL, { waitUntil: 'domcontentloaded' });

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

    let recovering = false;
    const recover = async (reason: string) => {
      if (recovering) return;
      recovering = true;
      console.warn(`${ts()} [main] recovering page (${reason})...`);
      try {
        await page.goto(PUMP_URL, { waitUntil: 'domcontentloaded' });
        console.log(`${ts()} [main] ✅ page recovered`);
        logTiming('recovered');
      } catch (err) {
        console.error(`${ts()} [main] in-place recovery failed, relaunching browser:`, err);
        endSession(`in-place recovery failed after: ${reason}`);
      } finally {
        recovering = false;
      }
    };

    page.on('crash', () => { void recover('page crashed (crash event)'); });
    page.on('close', () => endSession('page closed'));
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
            console.log(`${ts()} [poll] fetch=${fetchMs}ms reason=${reason} polls=${tickCount} trades=${tradesSeen} | ${stream?.describe()}`);
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
        // Self-contained (the site inserts them straight from the event). Record only: no alert type for
        // pump trades exists in the backend yet, so nothing is posted — see tradeLog.ts.
        const rec = tradeFromEvent(ev.raw, ev.receivedAtMs);
        if (rec) { logPumpTrade(rec); tradesSeen++; }
        return;
      }
      const lagMs = ev.createdAt ? ev.receivedAtMs - Date.parse(ev.createdAt) : NaN;
      logTiming('ring', ev.kind, ev.id, `serverToRingMs=${Number.isFinite(lagMs) ? lagMs : ''}`);
      void pollOnce(`ring:${ev.kind}`);
      if (followUpTimer) clearTimeout(followUpTimer);
      followUpTimer = setTimeout(() => { void pollOnce('ring-followup'); }, Math.max(1_200, lastMaxAgeS * 1_000 + 300));
    };

    // Safety net: slow while the live stream is healthy, otherwise as quick as the site's own contract
    // allows (never faster than maxAgeSeconds). Also drains a poll that had to wait for the minimum gap.
    pollInterval = setInterval(() => {
      const every = stream?.healthy() ? STREAM_FALLBACK_POLL_MS : Math.max(DEGRADED_POLL_MS, lastMaxAgeS * 1_000);
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
    if (probeInterval) clearInterval(probeInterval);
    if (pollInterval) clearInterval(pollInterval);
    if (followUpTimer) clearTimeout(followUpTimer);
    stream?.stop();
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
