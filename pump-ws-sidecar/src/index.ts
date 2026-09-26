import { chromium } from 'playwright';
import { postPumpEvent, postStructuredPump, heartbeat } from './client';
import { transformItem, type PumpFeedItem } from './transform';
import { runVerifiedSync } from './verifiedSync';
import { logTiming } from './timingLog';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
const PROFILE_DIR = './chromium-profile';
const PUMP_URL = 'https://pump.fun/';
const ALERTS_API = 'https://frontend-api-v3.pump.fun/following-positions/alerts?pageSize=20&kinds=callout,repost,reply&minTradeAmountUsd=10';
const HEARTBEAT_INTERVAL_MS = 30_000;
const POLL_INTERVAL_MS = 500;
const RATE_LIMIT_BACKOFF_MS = 30_000;
const FETCH_FAIL_BACKOFF_MS = 5_000;
// Remembered externalIds, so an in-flight item is not re-claimed by the next tick.
// 20 per page at 1/s — a few hundred covers many minutes of history.
const CLAIM_MEMORY = 500;
// The alerts fetch has a ~2.2s median round trip, so ONE in flight caps how often we can
// see the feed at ~2.2s no matter what POLL_INTERVAL_MS says. Overlapping fetches do not
// steal work (each returns the same page) — they shorten the gap between successive views
// of the feed. The claim set above is what makes that overlap safe.
const MAX_CONCURRENT_FETCHES = 4;
const PROBE_INTERVAL_MS = 15_000;
const PROBE_TIMEOUT_MS = 10_000;
const PROBE_FAILURES_BEFORE_RELOAD = 3;
const VERIFIED_SYNC_INTERVAL_MS = Number(process.env.VERIFIED_SYNC_INTERVAL_MS) || 6 * 60 * 60_000; // 6h default

interface AlertsProbeResult {
  ok: boolean;
  status?: number;
  data?: { items?: PumpFeedItem[] };
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
    ],
    ignoreDefaultArgs: ['--enable-automation'],
  });

  let hbInterval: NodeJS.Timeout | undefined;
  let probeInterval: NodeJS.Timeout | undefined;
  let pollInterval: NodeJS.Timeout | undefined;
  let verifiedSyncInterval: NodeJS.Timeout | undefined;

  try {
    const page = context.pages()[0] ?? await context.newPage();

    let endSession!: (reason: string) => void;
    const sessionEnded = new Promise<string>((resolve) => { endSession = resolve; });

    console.log(`${ts()} [main] navigating to`, PUMP_URL);
    await page.goto(PUMP_URL, { waitUntil: 'domcontentloaded' });

    if (firstRun) {
      // First run: no session yet. Give the user time to log into pump.fun by hand
      // in this (visible, HEADLESS=false) window — persists into PROFILE_DIR after that.
      console.log(`${ts()} [main] waiting for pump.fun login (up to 5 min)...`);
      let loggedIn = false;
      const deadline = Date.now() + 300_000;
      while (Date.now() < deadline) {
        const probe = await fetchAlerts(page).catch(() => ({ ok: false } as AlertsProbeResult));
        if (probe.ok) { loggedIn = true; break; }
        await new Promise((r) => setTimeout(r, 5_000));
      }
      if (!loggedIn) {
        console.error(`${ts()} [main] ❌ Login timed out after 5 minutes, exiting.`);
        process.exit(1);
      }
      console.log(`${ts()} [main] ✅ logged in, session persisted to ${PROFILE_DIR}`);
    }

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

    // Overlapping fetches are the point, not an optimisation. Each returns the same page,
    // so there is no work to steal — but with a ~2.2s round trip, ONE in flight caps how
    // often we can even SEE the feed at once every 2.2s, whatever POLL_INTERVAL_MS says.
    // Running several shortens the gap between successive views; measured 1157ms -> 527ms
    // and median end-to-end latency 4.72s -> 0.47s. Processing is also fired and forgotten
    // so a slow POST never stalls the loop, and items are claimed by externalId so the
    // overlap can never double-handle one.
    let inFlight = 0;
    let lastTickLog = 0;
    let tickCount = 0;
    let backoffUntil = 0;
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

    pollInterval = setInterval(() => {
      if (recovering || inFlight >= MAX_CONCURRENT_FETCHES || Date.now() < backoffUntil) return;
      inFlight++;
      tickCount++;
      void (async () => {
        try {
          const t0 = Date.now();
          const probe = await fetchAlerts(page);
          const fetchMs = Date.now() - t0;
          if (Date.now() - lastTickLog > 10_000) {
            lastTickLog = Date.now();
            console.log(`${ts()} [poll] fetch=${fetchMs}ms ticks/10s=${tickCount}`);
            logTiming('poll', `fetchMs=${fetchMs}`, `ticks10s=${tickCount}`, `inFlight=${inFlight}`);
            tickCount = 0;
          }
          if (!probe.ok) {
            // At 1/s a failing endpoint would be hammered 60x/min. Back off instead —
            // longer for 429 (we are being told to slow down) than for a transient error
            // or an expired session, which the health check recovers separately.
            const wait = probe.status === 429 ? RATE_LIMIT_BACKOFF_MS : FETCH_FAIL_BACKOFF_MS;
            backoffUntil = Date.now() + wait;
            console.warn(`${ts()} [poll] alerts fetch failed (status=${probe.status ?? 'n/a'}) — backing off ${wait / 1000}s`);
            logTiming('fail', `status=${probe.status ?? 'n/a'}`, `fetchMs=${fetchMs}`, `backoffMs=${wait}`);
            return;
          }

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
          console.error(`${ts()} [poll] error:`, err);
        } finally {
          inFlight--;
        }
      })();
    }, POLL_INTERVAL_MS);

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

    console.log(`${ts()} [main] Sidecar running — polling pump.fun alerts every ${POLL_INTERVAL_MS}ms, verified-trader sync disabled`);
    logTiming('start');

    const reason = await sessionEnded;
    console.warn(`${ts()} [main] session ended: ${reason}`);
  } finally {
    if (hbInterval) clearInterval(hbInterval);
    if (probeInterval) clearInterval(probeInterval);
    if (pollInterval) clearInterval(pollInterval);
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
