import { chromium } from 'playwright';
import { attachWsInterceptor } from './ws-intercept';
import { existsSync, unlinkSync } from 'fs';
import { join } from 'path';
import { postStructured, heartbeat } from './client';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}] [${ACCOUNT_NAME}]`; };

const HEADLESS = process.env.HEADLESS === 'true';
// Each fomo.family account (distinct followings, no overlap) gets its own persistent
// profile dir so multiple accounts can run concurrently as separate processes, all
// posting to the same backend. Defaults preserve the original single-session setup.
const PROFILE_DIR = process.env.PROFILE_DIR ?? './chromium-profile';
const ACCOUNT_NAME = process.env.ACCOUNT_NAME ?? 'default';
const FOMO_URL = 'https://fomo.family/';
const HEARTBEAT_INTERVAL_MS = 30_000;
// Renderer health check: a crashed ("Aw, Snap!") page rejects evaluate() with "Target crashed"
const PROBE_INTERVAL_MS = 15_000;
const PROBE_TIMEOUT_MS = 10_000;
const PROBE_FAILURES_BEFORE_RELOAD = 3;
// A page left open for days keeps growing, and reloading it in place does not give the memory back. So the page is ROTATED: a second page
// opens and starts receiving the trades while the first keeps working, and only when the new one is really receiving does the old one close
// (its process, and the memory in it, goes away). This sidecar has no fallback poll, which is exactly why the old page is never closed first.
// A trade received by both pages is posted once (transformFrame's dedup by trade id).
const PAGE_REFRESH_MS = (Number(process.env.PAGE_REFRESH_HOURS) || 12) * 60 * 60_000;
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

// One browser session; returns when a full relaunch is needed (main() handles that)
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

  // The page is only listened to: pictures, video and fonts are refused before they download (the trade feed's token and avatar images
  // otherwise fill the tab). Network sockets and the app's own requests are not touched. FOMO_LEAN_PAGE=0 turns it off.
  if (process.env.FOMO_LEAN_PAGE !== '0') {
    await context.route('**/*', (route) => {
      const t = route.request().resourceType();
      if (t === 'image' || t === 'media' || t === 'font') void route.abort(); else void route.continue();
    });
  }

  let hbInterval: NodeJS.Timeout | undefined;
  let probeInterval: NodeJS.Timeout | undefined;
  let refreshTimer: NodeJS.Timeout | undefined;
  let triggerInterval: NodeJS.Timeout | undefined;

  try {
    let page = context.pages()[0] ?? await context.newPage();
    // Trades seen by each page while two pages overlap during a rotation (null the rest of the time): the evidence that nothing is lost.
    let overlap: { oldPage: import('playwright').Page; old: Set<string>; neu: Set<string> } | null = null;
    const seenBy = (p: import('playwright').Page, id: string) => { const o = overlap; if (o) (p === o.oldPage ? o.old : o.neu).add(id); };
    const firstPage = page;

    let recovering = false;
    let openTaps = 0;

    let endSession!: (reason: string) => void;
    const sessionEnded = new Promise<string>((resolve) => { endSession = resolve; });

    // Log-only: the page reconnects its own WS, we never reload speculatively
    attachWsInterceptor(page, postStructured, {
      onSeen: (id) => seenBy(firstPage, id),
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
      // The trade WebSocket opening is proof enough that the session works: waiting only for some text on the page once ran out
      // on a page that was fine, and exiting here took the whole sidecar (and every FOMO alert) down. It never exits any more.
      const deadline = Date.now() + 300_000;
      let loggedIn = false;
      while (Date.now() < deadline && !loggedIn) {
        loggedIn = openTaps > 0 || await page.locator('text=/following|alerts|feed/i').first().isVisible().catch(() => false);
        if (!loggedIn) await sleep(2_000);
      }
      if (!loggedIn) console.warn(`${ts()} [main] ⚠️ login not confirmed after 5 minutes, carrying on anyway`);
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

    // Only the CURRENT page matters: the old one is closed on purpose during a rotation, and a page that never became current is ignored.
    const watch = (p: import('playwright').Page) => {
      p.on('crash', () => { if (p === page) void recover('page crashed (crash event)'); });
      p.on('close', () => { if (p === page) endSession('page closed'); });
    };
    watch(page);
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

    let rotating = false;
    const rotatePage = async (why: string): Promise<boolean> => {
      if (rotating || recovering) return false;
      rotating = true;
      const old = page;
      let fresh: import('playwright').Page | undefined;
      try {
        console.log(`${ts()} [rotate] start (${why}): a second page opens while the first keeps working`);
        overlap = { oldPage: old, old: new Set(), neu: new Set() };
        fresh = await context.newPage();
        const freshPage = fresh;
        let freshTaps = 0;
        watch(fresh);
        attachWsInterceptor(fresh, postStructured, {
          onSeen: (id) => seenBy(freshPage, id),
          onTapOpen: () => { freshTaps++; openTaps++; console.log(`${ts()} [rotate] new page: tap WS open`); },
          onTapClose: () => { freshTaps = Math.max(0, freshTaps - 1); openTaps = Math.max(0, openTaps - 1); console.warn(`${ts()} [rotate] new page: tap WS closed`); },
        });
        await fresh.goto(FOMO_URL, { waitUntil: 'domcontentloaded' });
        // The new page must really be receiving trades before the old one is touched: some trades on it, and it keeps up with the old page.
        const startedAt = Date.now();
        while (Date.now() - startedAt < 120_000) {
          const o = overlap;
          const elapsed = Date.now() - startedAt;
          if (freshTaps > 0 && o.neu.size >= 3 && elapsed >= 20_000) break;
          if (freshTaps > 0 && o.neu.size >= 1 && elapsed >= 45_000) break;
          await sleep(1_000);
        }
        const o = overlap;
        if (freshTaps === 0 || o.neu.size === 0) throw new Error(`the new page was not receiving trades after ${Math.round((Date.now() - startedAt) / 1000)} s, the old page stays`);
        console.log(`${ts()} [rotate] new page is receiving (${o.neu.size} trades so far), both pages overlap a little longer`);
        await sleep(10_000);                       // both listen at once: nothing can fall between them
        if (freshTaps === 0) throw new Error('the new page lost its connection during the overlap, the old page stays');
        const onlyOld = [...o.old].filter((id) => !o.neu.has(id)).length, onlyNew = [...o.neu].filter((id) => !o.old.has(id)).length;
        page = fresh;                              // from here on, the new page is the one that counts
        await old.close().catch(() => { /* already gone */ });
        console.log(`${ts()} [rotate] done: old page closed. During the overlap the old page saw ${o.old.size} trades, the new ${o.neu.size}, only-old ${onlyOld}, only-new ${onlyNew}`);
        return true;
      } catch (err) {
        console.warn(`${ts()} [rotate] given up, nothing lost: ${String(err).slice(0, 200)}`);
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

    // Manual trigger for tests and emergencies: create the file logs/rotate-now and the next check (5 s) rotates once.
    const triggerFile = join(__dirname, '..', 'logs', 'rotate-now');
    triggerInterval = setInterval(() => {
      if (!existsSync(triggerFile)) return;
      try { unlinkSync(triggerFile); } catch { /* gone already */ }
      void rotatePage('requested by file');
    }, 5_000);

    console.log(`${ts()} [main] Sidecar running — intercepting WS trade events`);

    const reason = await sessionEnded;
    console.warn(`${ts()} [main] session ended: ${reason}`);
  } finally {
    if (hbInterval) clearInterval(hbInterval);
    if (probeInterval) clearInterval(probeInterval);
    if (refreshTimer) clearTimeout(refreshTimer);
    if (triggerInterval) clearInterval(triggerInterval);
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
