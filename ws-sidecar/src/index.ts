import { chromium } from 'playwright';
import { attachWsInterceptor } from './ws-intercept';
import { postStructured, heartbeat } from './client';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
const PROFILE_DIR = './chromium-profile';
const FOMO_URL = 'https://fomo.family/';
const HEARTBEAT_INTERVAL_MS = 30_000;

async function run(): Promise<void> {
  console.log(`${ts()} [main] launching browser (headless=%s)`, HEADLESS);

  const context = await chromium.launchPersistentContext(PROFILE_DIR, {
    channel: 'chrome',
    headless: HEADLESS,
    args: [
      '--disable-blink-features=AutomationControlled',
    ],
    ignoreDefaultArgs: ['--enable-automation'],
  });

  const page = context.pages()[0] ?? await context.newPage();

  attachWsInterceptor(page, postStructured);

  console.log(`${ts()} [main] navigating to`, FOMO_URL);
  await page.goto(FOMO_URL, { waitUntil: 'domcontentloaded' });

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

  // Start heartbeat
  const hbInterval = setInterval(() => {
    heartbeat().catch(() => {});
  }, HEARTBEAT_INTERVAL_MS);

  console.log(`${ts()} [main] Sidecar running — intercepting WS trade events`);

  // Keep process alive; reconnect on page crash
  page.on('crash', async () => {
    console.warn(`${ts()} [main] Page crashed, reloading...`);
    clearInterval(hbInterval);
    await context.close().catch(() => {});
    // Restart from scratch after a short delay
    setTimeout(() => run().catch(console.error), 3_000);
  });

  // Prevent the process from exiting
  await new Promise<never>(() => {});
}

run().catch((err) => {
  console.error(`${ts()} [main] Fatal error:`, err);
  process.exit(1);
});
