// Public pump.fun profile data for every Pump trader we have a userId for:
// leaderboard-stats, portfolio (PnL), full callout history, and the PnL leaderboards.
const fs = require('fs');
const D = __dirname + '/data/';
const OUT = D + 'pump_profiles/';
fs.mkdirSync(OUT, { recursive: true });
const B = 'https://frontend-api-v3.pump.fun';
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function get(path) {
  for (let i = 0; i < 8; i++) {
    try {
      const r = await fetch(B + path, { headers: { 'User-Agent': 'Mozilla/5.0', Accept: 'application/json' } });
      if (r.ok) return await r.json();
      if (r.status === 400 || r.status === 401 || r.status === 404) return { err: r.status };
      await sleep(r.status === 429 ? 4000 * (i + 1) : 400 * (i + 1));
    } catch { await sleep(1000 * (i + 1)); }
  }
  return { err: 'retries' };
}

const MAX_CALLOUTS = 1000;
async function scrapeUser(uid, handle, wallet) {
  const f = OUT + uid + '.json';
  if (fs.existsSync(f) && JSON.parse(fs.readFileSync(f)).callouts.length) return;
  const stats = await get(`/callout/leaderboard-stats/${uid}`);
  const portfolio = await get(`/user-portfolio/${uid}`);
  const rank = wallet ? await get(`/pnl-leaderboard/projected-rank?wallet=${wallet}`) : null;
  const callouts = [];
  let token = null;
  do {
    const r = await get(`/callout/list/${uid}?limit=100&sortBy=TIMESTAMP&sortOrder=DESC${token ? '&pageToken=' + token : ''}`);
    if (!r.callouts) break;
    for (const c of r.callouts) callouts.push({ id: c.calloutId, mint: c.coinMint, chain: c.chainId, t: c.createdAt, mcap: c.marketCap,
      mult: c.multiple, maxMult: c.maxMultiplier, maxAt: c.maxMultiplierAt, likes: c.likes, views: c.viewCount, quote: !!c.quotedCalloutId });
    token = r.nextPageToken;
  } while (token && callouts.length < MAX_CALLOUTS);
  const pos = (portfolio.positions || []).map(p => ({ mint: p.coinMint, exited: p.isExited, pnl: p.pnlUsd, rpnl: p.realizedPnlUsd,
    cost: p.costBasisUsd, bought: p.amountBoughtUsd, value: p.valueUsd, spam: p.potentialSpam }));
  fs.writeFileSync(f, JSON.stringify({ uid, handle, wallet, rank, stats, summary: portfolio.summary, positions: pos, callouts }));
}

(async () => {
  // pump callers we know: handle -> userId from captured events
  const calls = JSON.parse(fs.readFileSync(D + 'pump_callouts.json'));
  const users = new Map();
  for (const c of calls) if (c.uid) users.set(c.uid, [c.h, c.w]);
  console.log('pump users', users.size);
  const list = [...users]; let i = 0, done = 0;
  await Promise.all(Array.from({ length: 4 }, async () => {
    while (i < list.length) { const [uid, [h, w]] = list[i++]; await scrapeUser(uid, h, w); if (++done % 25 === 0) console.log('users', done); }
  }));
  // PnL leaderboards (wallet-keyed) — page through each period
  for (const period of ['daily', 'weekly', 'monthly']) {
    const f = `${D}pump_pnl_${period}.json`; if (fs.existsSync(f)) continue;
    const entries = [];
    for (let p = 0; p < 1; p++) {
      const r = await get(`/pnl-leaderboard?period=${period}&limit=1000`);
      if (!r.entries || !r.entries.length) break;
      entries.push(...r.entries.map(e => ({ rank: e.rank, wallet: e.walletAddress, pnlUsd: e.pnlUsd, pnlPct: e.pnlPercent, rpnl: e.realizedPnlUsd, upnl: e.unrealizedPnlUsd, n: e.positionsCount })));
    }
    fs.writeFileSync(f, JSON.stringify(entries));
    console.log(period, entries.length, 'max rank', entries.at(-1)?.rank);
  }
})();
