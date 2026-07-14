import { chromium } from 'playwright';
import { attachWsInterceptor } from './ws-intercept';
import { postStructured, heartbeat } from './client';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
const PROFILE_DIR = './chromium-profile';
const FOMO_URL = 'https://fomo.family/';
const HEARTBEAT_INTERVAL_MS = 30_000;
// Renderer health check: a crashed ("Aw, Snap!") page rejects evaluate() with "Target crashed"
const PROBE_INTERVAL_MS = 15_000;
const PROBE_TIMEOUT_MS = 10_000;
const PROBE_FAILURES_BEFORE_RELOAD = 3;

// One browser session; returns when a full relaunch is needed (main() handles that)
async function session(firstRun: boolean): Promise<void> {
  console.log(`${ts()} [main] launching browser (headless=%s)`, HEADLESS);

  const context = await chromium.launchPersistentContext(PROFILE_DIR, {
    channel: 'chrome',
    headless: HEADLESS,
    args: [
      '--disable-blink-features=AutomationControlled',
    ],
    ignoreDefaultArgs: ['--enable-automation'],
  });

  let hbInterval: NodeJS.Timeout | undefined;
  let probeInterval: NodeJS.Timeout | undefined;

  try {
    const page = context.pages()[0] ?? await context.newPage();

    let recovering = false;
    let openTaps = 0;

    let endSession!: (reason: string) => void;
    const sessionEnded = new Promise<string>((resolve) => { endSession = resolve; });

    // Log-only: the page reconnects its own WS, we never reload speculatively
    attachWsInterceptor(page, postStructured, {
      onTapOpen: () => {
        openTaps++;
        console.log(`${ts()} [main] tap WS open (${openTaps} active)`);
      },
      onTapClose: () => {
        openTaps = Math.max(0, openTaps - 1);
        console.warn(`${ts()} [main] tap WS closed (${openTaps} active) — page should reconnect on its own`);
      },
    });

    const gotoFeed = async () => {
      await page.goto(FOMO_URL, { waitUntil: 'domcontentloaded' });
    };

    console.log(`${ts()} [main] navigating to`, FOMO_URL);
    await gotoFeed();

    if (firstRun) {
      // On first run the session won't exist — give the user time to log in manually.
      // After that the chromium-profile persists the session and this resolves instantly.
      const loggedIn = await page.locator('text=/following|alerts|feed/i')
        .first()
        .waitFor({ timeout: 300_000 })
        .then(() => true)
        .catch(() => false);

      if (!loggedIn) {
        console.error(`${ts()} [main] ❌ Login timed out after 5 minutes, exiting.`);
        process.exit(1);
      }
    }

    // Reload the tab in place (fresh renderer); escalate to full relaunch if even that fails
    const recover = async (reason: string) => {
      if (recovering) return;
      recovering = true;
      console.warn(`${ts()} [main] recovering page (${reason})...`);
      try {
        await gotoFeed();
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

    hbInterval = setInterval(() => { heartbeat().catch(() => {}); }, HEARTBEAT_INTERVAL_MS);

    // Health check — backstop in case the crash event never fires
    let probeFailures = 0;
    let probing = false;
    probeInterval = setInterval(() => {
      if (recovering || probing) return;
      probing = true;
      void (async () => {
        try {
          await Promise.race([
            page.evaluate('1'),
            new Promise((_, reject) =>
              setTimeout(() => reject(new Error('probe timed out')), PROBE_TIMEOUT_MS)),
          ]);
          probeFailures = 0;
        } catch (err) {
          const msg = String(err);
          if (/crash/i.test(msg)) {
            void recover('renderer crashed (health check)');
            return;
          }
          if (/context or browser has been closed/i.test(msg)) {
            endSession('browser closed (probe)');
            return;
          }
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

    console.log(`${ts()} [main] Sidecar running — intercepting WS trade events`);

    const reason = await sessionEnded;
    console.warn(`${ts()} [main] session ended: ${reason}`);
  } finally {
    if (hbInterval) clearInterval(hbInterval);
    if (probeInterval) clearInterval(probeInterval);
    await context.close().catch(() => {});
  }
}

// Keep the sidecar alive forever — failed launches (e.g. locked profile) just retry
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
