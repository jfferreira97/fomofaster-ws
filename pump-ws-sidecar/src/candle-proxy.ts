import { chromium, type BrowserContext, type Page } from 'playwright';
import { createServer } from 'http';

// pump.fun's candle route (frontend-api-v3.pump.fun/candles/...) answers this machine with a Cloudflare challenge page when it is
// called from a plain HTTP client, so the backend could no longer replay calls (trader ratings) or draw charts from it. A real Chrome
// that has passed Cloudflare is let through. This process keeps one such Chrome (its OWN profile, never the alert sidecars' one) and
// serves those requests to the backend, on this machine only:
//   GET http://127.0.0.1:8130/fetch?url=<a frontend-api-v3.pump.fun/candles URL>[&prio=1]  -> the upstream status and JSON body
//   GET http://127.0.0.1:8130/health                                              -> ready, counters
// It only ever fetches candle URLs, one at a time, at most one every PACE_MS, so it never hammers pump.fun (2 requests a second
// to the alerts API once got this IP challenged), and slows down by itself when pump.fun answers 429. The page sits on a tiny pump.fun page (robots.txt) and refuses pictures, video and
// fonts, so it stays small. When a fetch meets the challenge again, it re-opens pump.fun once and retries.
const PORT = Number(process.env.CANDLE_PROXY_PORT) || 8130;
const PROFILE_DIR = '../candle-profile';   // outside pump-ws-sidecar, so nothing that looks for the alert sidecar by its folder ever mistakes this Chrome for it
const PACE_MS = Number(process.env.CANDLE_PACE_MS) || 700;
const ALLOWED = /^https:\/\/frontend-api-v3\.pump\.fun\/candles\/[A-Za-z0-9:_-]+\/[A-Za-z0-9]+\?[A-Za-z0-9=&_.-]*$/;

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

let ctx: BrowserContext | undefined;
let page: Page | undefined;
let ready = false;
let warming: Promise<boolean> | null = null;
const stats = { served: 0, failed: 0, challenged: 0, rewarms: 0, limited: 0, dropped: 0, paceMs: PACE_MS, byStatus: {} as Record<string, number>, lastOkAt: '' as string, startedAt: new Date().toISOString() };

async function launch(): Promise<void> {
  ctx = await chromium.launchPersistentContext(PROFILE_DIR, {
    channel: 'chrome',
    headless: false,
    viewport: { width: 900, height: 620 },
    args: ['--disable-blink-features=AutomationControlled', '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows'],
    ignoreDefaultArgs: ['--enable-automation'],
  });
  page = ctx.pages()[0] ?? await ctx.newPage();
  await page.route('**/*', (route) => {
    const t = route.request().resourceType();
    if (t === 'image' || t === 'media' || t === 'font') void route.abort(); else void route.continue();
  });
  ctx.on('close', () => { console.error(`${ts()} [candles] browser closed, exiting so the supervisor restarts it`); process.exit(1); });
}

// Open pump.fun until Cloudflare lets the page through, then park on the smallest pump.fun page.
async function warm(): Promise<boolean> {
  if (warming) return warming;
  warming = (async () => {
    ready = false;
    try {
      await page!.goto('https://pump.fun/', { waitUntil: 'domcontentloaded', timeout: 60_000 });
      for (let i = 0; i < 60; i++) {
        const title = await page!.title().catch(() => '');
        if (title && !/just a moment|attention required/i.test(title)) break;
        await sleep(1000);
      }
      await page!.goto('https://pump.fun/robots.txt', { waitUntil: 'domcontentloaded', timeout: 30_000 }).catch(() => {});
      const probe = await get('https://frontend-api-v3.pump.fun/candles/solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp/So11111111111111111111111111111111111111112?res=1m&count=1&to=' + Date.now());
      ready = probe.status === 200 || probe.status === 400 || probe.status === 404;
      console.log(`${ts()} [candles] warm-up ${ready ? 'ok' : 'NOT ok (status ' + probe.status + ')'}`);
      return ready;
    } catch (e) {
      console.error(`${ts()} [candles] warm-up failed: ${String(e).slice(0, 160)}`);
      return false;
    } finally { warming = null; }
  })();
  return warming;
}

async function get(url: string): Promise<{ status: number; body: string }> {
  return page!.evaluate(async (u) => {
    try { const r = await fetch(u, { credentials: 'include' }); return { status: r.status, body: await r.text() }; }
    catch (e) { return { status: 599, body: String(e) }; }
  }, url);
}

// one request at a time, paced. Priority requests (a chart someone is looking at, ?prio=1) go before the queued background ones
// (the trader ratings replay, which can keep the queue full for hours).
// pump.fun rate-limits the candle route (429) well before PACE_MS on a long run, so the gap adapts: every 429 doubles it (up to
// 8 seconds) and holds the queue for that long, every answer that gets through shortens it a little, back down to PACE_MS.
// A request whose caller has already given up (the backend times out) is dropped from the queue without costing a fetch.
type Answer = { status: number; body: string };
const GONE: Answer = { status: 499, body: '' };
const waiting: { prio: boolean; alive: () => boolean; run: () => Promise<void>; drop: () => void }[] = [];
let busy = false;
let lastAt = 0;
let pace = PACE_MS;
async function drain(): Promise<void> {
  if (busy) return;
  busy = true;
  while (waiting.length) {
    const i = waiting.findIndex((w) => w.prio);
    const next = waiting.splice(i >= 0 ? i : 0, 1)[0];
    if (!next.alive()) { stats.dropped++; next.drop(); continue; }
    const wait = lastAt + (next.prio ? PACE_MS : pace) - Date.now();   // a chart waits only the base gap; a 429 still slows it
    if (wait > 0) await sleep(wait);
    if (!next.alive()) { stats.dropped++; next.drop(); continue; }
    lastAt = Date.now();
    await next.run();
  }
  busy = false;
}
function paced(url: string, prio: boolean, alive: () => boolean): Promise<Answer> {
  return new Promise<Answer>((resolve, reject) => {
    waiting.push({ prio, alive, run: () => get(url).then(resolve, reject), drop: () => resolve(GONE) });
    void drain();
  });
}

async function fetchCandles(url: string, prio: boolean, alive: () => boolean): Promise<Answer> {
  if (!ready && !(await warm())) return { status: 503, body: '{"error":"not past Cloudflare yet"}' };
  let r = await paced(url, prio, alive);
  for (let i = 0; r.status === 429 && i < 4; i++) {
    stats.limited++;
    pace = Math.min(8000, pace * 2);
    lastAt = Date.now() + pace;   // the next request, this retry included, waits twice the gap
    r = await paced(url, prio, alive);
  }
  if (r.status === 200) pace = Math.max(PACE_MS, pace * 0.97);
  stats.paceMs = Math.round(pace);
  if (r.status === 403 || /just a moment/i.test(r.body.slice(0, 300))) {
    stats.challenged++;
    stats.rewarms++;
    if (await warm()) r = await paced(url, prio, alive);
  }
  return r;
}

async function main(): Promise<void> {
  await launch();
  await warm();
  createServer(async (req, res) => {
    const u = new URL(req.url || '/', 'http://localhost');
    if (u.pathname === '/health') {
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ ready, queued: waiting.length, ...stats }));
      return;
    }
    if (u.pathname !== '/fetch') { res.writeHead(404); res.end(); return; }
    const target = u.searchParams.get('url') || '';
    if (!ALLOWED.test(target)) { res.writeHead(400, { 'Content-Type': 'application/json' }); res.end('{"error":"only pump.fun candle URLs"}'); return; }
    let gone = false;
    res.on('close', () => { gone = true; });
    try {
      const r = await fetchCandles(target, u.searchParams.get('prio') === '1', () => !gone);
      if (r === GONE) return;
      const challenged = r.status === 403 || /just a moment/i.test(r.body.slice(0, 300));
      stats.byStatus[r.status] = (stats.byStatus[r.status] || 0) + 1;
      if (r.status === 200) { stats.served++; stats.lastOkAt = new Date().toISOString(); } else stats.failed++;
      res.writeHead(challenged ? 502 : r.status, { 'Content-Type': 'application/json' });
      res.end(challenged ? '{"error":"Cloudflare challenge"}' : r.body);
    } catch (e) {
      stats.failed++;
      res.writeHead(502, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: String(e).slice(0, 200) }));
    }
  }).listen(PORT, '127.0.0.1', () => console.log(`${ts()} [candles] serving pump.fun candles on http://127.0.0.1:${PORT}`));
}

main().catch((e) => { console.error(`${ts()} [candles] fatal:`, e); process.exit(1); });
