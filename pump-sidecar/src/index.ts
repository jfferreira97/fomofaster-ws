import { chromium } from 'playwright';
import { postPumpEvent, postStructuredPump, heartbeat } from './client';
import { transformItem, type PumpFeedItem } from './transform';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
const PROFILE_DIR = './chromium-profile';
const PUMP_URL = 'https://pump.fun/';
const ALERTS_API = 'https://frontend-api-v3.pump.fun/following-positions/alerts?pageSize=20&kinds=callout,repost,reply&minTradeAmountUsd=10';
const HEARTBEAT_INTERVAL_MS = 30_000;
const POLL_INTERVAL_MS = 20_000;
const PROBE_INTERVAL_MS = 15_000;
const PROBE_TIMEOUT_MS = 10_000;
const PROBE_FAILURES_BEFORE_RELOAD = 3;

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
    args: ['--disable-blink-features=AutomationControlled'],
    ignoreDefaultArgs: ['--enable-automation'],
  });

  let hbInterval: NodeJS.Timeout | undefined;
  let probeInterval: NodeJS.Timeout | undefined;
  let pollInterval: NodeJS.Timeout | undefined;

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

    let polling = false;
    pollInterval = setInterval(() => {
      if (recovering || polling) return;
      polling = true;
      void (async () => {
        try {
          const probe = await fetchAlerts(page);
          if (!probe.ok) {
            console.warn(`${ts()} [poll] alerts fetch failed (status=${probe.status ?? 'n/a'}) — session may have expired`);
            return;
          }
          const items = probe.data?.items ?? [];
          for (const item of items) {
            const transformed = transformItem(item);
            if (!transformed) continue; // update/quote — not ingested (see HANDOFF.md)

            const accepted = await postPumpEvent({ ...item, externalId: transformed.externalId });
            if (!accepted) continue; // duplicate — backend already processed this one

            await postStructuredPump(transformed.structured);
          }
        } catch (err) {
          console.error(`${ts()} [poll] error:`, err);
        } finally {
          polling = false;
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

    console.log(`${ts()} [main] Sidecar running — polling pump.fun alerts every ${POLL_INTERVAL_MS / 1000}s`);

    const reason = await sessionEnded;
    console.warn(`${ts()} [main] session ended: ${reason}`);
  } finally {
    if (hbInterval) clearInterval(hbInterval);
    if (probeInterval) clearInterval(probeInterval);
    if (pollInterval) clearInterval(pollInterval);
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
    await new Promise((resolve) => setTimeout(resolve, 5_000));
  }
}

main().catch((err) => {
  console.error(`${ts()} [main] Fatal error:`, err);
  process.exit(1);
});
