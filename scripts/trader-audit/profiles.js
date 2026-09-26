// Full-history trader profiles that don't need price candles:
// FOMO from our own trade tape (since May 18), Pump from scraped public profiles + our callouts.
// Output: data/profiles.json
const fs = require('fs');
const D = __dirname + '/data/';
const q = (arr, p) => { arr = arr.filter(x => x != null && !isNaN(x)); if (!arr.length) return null; const s = [...arr].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(p * s.length))]; };
const rate = (arr, f) => arr.length ? arr.filter(f).length / arr.length : null;
const tsOf = s => Date.parse(s.endsWith('Z') ? s : s.replace(' ', 'T') + 'Z');
const CHAIN = { 1399811149: 'SOL', 4663: 'ROBINHOOD', 56: 'BNB', 8453: 'BASE', 1: 'ETH', 5042: 'ARC', 143: 'MONAD' };

const profiles = {};
const P = (platform, h) => profiles[`${platform}|${h}`] ||= { platform, h };

// ---------- FOMO ----------
const calls = JSON.parse(fs.readFileSync(D + 'calls.json'));
const trades = JSON.parse(fs.readFileSync(D + 'fomo_trades.json'));
const lastEq = {}, firstT = {}, lastT = {}, buyUsd = {}, chains = {};
for (const t of trades) {
  const tt = tsOf(t.c);
  if (t.e) lastEq[t.h] = t.e;
  firstT[t.h] ??= tt; lastT[t.h] = tt;
  if (t.t === 'swap_buy') { (buyUsd[t.h] ||= []).push(t.u); (chains[t.h] ||= {})[CHAIN[t.n] || t.n] = ((chains[t.h] ||= {})[CHAIN[t.n] || t.n] || 0) + 1; }
}
const fomoCalls = calls.filter(c => c.platform === 'fomo');
const byH = {};
for (const c of fomoCalls) (byH[c.h] ||= []).push(c);
for (const [h, cs] of Object.entries(byH)) {
  const p = P('fomo', h);
  const own = cs.filter(c => c.roi != null);
  const spanDays = Math.max(1, (lastT[h] - firstT[h]) / 864e5);
  Object.assign(p, {
    entries: cs.length, entriesPerDay: cs.length / spanDays, spanDays,
    mcapMedian: q(cs.map(c => c.m), 0.5), mcapP25: q(cs.map(c => c.m), 0.25),
    under50k: rate(cs, c => c.m < 50e3), under250k: rate(cs, c => c.m < 250e3), over10m: rate(cs, c => c.m > 10e6),
    buySizeMedian: q(buyUsd[h] || [], 0.5), equity: lastEq[h] ?? null,
    chains: chains[h] || {},
    roundTrips: own.length, ownWin: rate(own, c => c.roi > 0), ownRoiMedian: q(own.map(c => c.roi), 0.5),
    ownBig: rate(own, c => c.roi >= 1), // closed a 2x+
    holdMedianMin: q(cs.map(c => c.holdMin), 0.5), holdP25Min: q(cs.map(c => c.holdMin), 0.25),
    realizedUsd: own.reduce((s, c) => s + c.soldUsd - c.costUsd, 0),
    fastDump: rate(cs, c => c.sold10mFrac >= 0.5), devShare: rate(cs, c => c.dev),
  });
}
// on-chain PnL for matched Solana wallets (pump.fun's public per-wallet PnL leaderboard)
if (fs.existsSync(D + 'fomo_wallet_pnl.json')) {
  for (const [h, w] of Object.entries(JSON.parse(fs.readFileSync(D + 'fomo_wallet_pnl.json')))) {
    const p = P('fomo', h);
    for (const per of ['daily', 'weekly', 'monthly']) if (w[per]) {
      p[`pnlRank_${per}`] = w[per].rank; p[`pnlUsd_${per}`] = w[per].pnlUsd; p[`pnlPct_${per}`] = w[per].pnlPct;
    }
  }
}
// thesis posts: count, mcap, author's own reported PnL on that token
const thesis = JSON.parse(fs.readFileSync(D + 'fomo_thesis.json'));
const th = {};
for (const t of thesis) (th[t.h] ||= []).push(t);
for (const [h, ts] of Object.entries(th)) {
  const p = P('fomo', h);
  p.thesisCount = ts.length;
  p.thesisMcapMedian = q(ts.map(t => +t.m), 0.5);
  p.thesisUnder50k = rate(ts.filter(t => t.m), t => +t.m < 50e3);
}

// ---------- Pump ----------
const pc = JSON.parse(fs.readFileSync(D + 'pump_callouts.json'));
const pBy = {};
for (const c of pc) (pBy[c.h] ||= []).push(c);
const wallets = {};
for (const [h, cs] of Object.entries(pBy)) {
  const p = P('pump', h);
  const t0 = tsOf(cs[0].c), t1 = tsOf(cs.at(-1).c);
  wallets[cs[0].w] = h;
  Object.assign(p, {
    uid: cs[0].uid, wallet: cs[0].w, x: cs[0].x,
    entries: cs.length, entriesPerDay: cs.length / Math.max(1, (t1 - t0) / 864e5),
    mcapMedian: q(cs.map(c => +c.m), 0.5), mcapP25: q(cs.map(c => +c.m), 0.25),
    under50k: rate(cs, c => +c.m < 50e3), under250k: rate(cs, c => +c.m < 250e3), over10m: rate(cs, c => +c.m > 10e6),
    quoteShare: rate(cs, c => !!c.q),
    chains: cs.reduce((m, c) => (m[CHAIN[c.n] || c.n] = (m[CHAIN[c.n] || c.n] || 0) + 1, m), {}),
  });
}
// pump PnL leaderboard position for a wallet (daily/weekly/monthly), public per-wallet lookup
const pnlRanks = r => { const o = {}; for (const per of ['daily', 'weekly', 'monthly']) { const e = r?.[per]; if (e?.found) { o[`pnlRank_${per}`] = e.projectedRank; o[`pnlUsd_${per}`] = e.entry?.pnlUsd ?? null; o[`pnlPct_${per}`] = e.entry?.pnlPercent ?? null; } } return o; };
// scraped public profile: full callout history (pump's own maxMultiplier), leaderboards, portfolio PnL
const pdir = D + 'pump_profiles/';
if (fs.existsSync(pdir)) for (const f of fs.readdirSync(pdir)) {
  const j = JSON.parse(fs.readFileSync(pdir + f));
  const p = P('pump', j.handle);
  const cs = j.callouts.filter(c => !c.quote);
  const mm = cs.map(c => c.maxMult).filter(x => x > 0);
  const st = j.stats || {};
  Object.assign(p, {
    histCallouts: j.callouts.length, histFirst: cs.length ? Math.min(...cs.map(c => c.t)) : null,
    histMaxMultMedian: q(mm, 0.5), hist2x: rate(mm, x => x >= 2), hist5x: rate(mm, x => x >= 5), hist10x: rate(mm, x => x >= 10),
    histMcapMedian: q(cs.map(c => c.mcap), 0.5),
    // how fast the peak came (pump reports when the max multiple was hit)
    histTimeToMaxMedMin: q(cs.filter(c => c.maxAt && c.maxMult >= 1.5).map(c => (Date.parse(c.maxAt) - c.t) / 60e3), 0.5),
    viewsMedian: q(cs.map(c => c.views), 0.5),
    lbWeeklyTop10: st.weekly?.top10?.appearances ?? null, lbWeeklyTop50: st.weekly?.top50?.appearances ?? null,
    lbMonthlyTop10: st.monthly?.top10?.appearances ?? null, lbMonthlyTop50: st.monthly?.top50?.appearances ?? null,
    lbBestRank: st.bestRank?.rank ?? null, lbBestPeriod: st.bestRank?.periodLabel ?? null,
    bestCalloutMult: st.bestCallout?.multiple ?? null,
    ...pnlRanks(j.rank),
    pnlTotalUsd: j.summary?.totalPnlUsd ?? null, pnlTotalPct: j.summary?.totalPnlPercentage ?? null, portfolioValueUsd: j.summary?.totalValueUsd ?? null,
    realizedUsd: j.positions.filter(x => !x.spam).reduce((s, x) => s + (x.rpnl || 0), 0),
    positionsExited: j.positions.filter(x => x.exited && !x.spam).length,
    exitedWin: rate(j.positions.filter(x => x.exited && !x.spam && x.rpnl != null), x => x.rpnl > 0),
  });
}
// every tracked Pump trader (scrape_pump_all.js): profile, pump's own callout stats, per-wallet
// PnL leaderboard, portfolio. Fills in traders who never posted a callout in our window too.
if (fs.existsSync(D + 'pump_users/')) for (const f of fs.readdirSync(D + 'pump_users/')) {
  const u = JSON.parse(fs.readFileSync(D + 'pump_users/' + f));
  if (!u.found) continue;
  const p = P('pump', u.h);
  const cs = u.calloutStats?.all || u.calloutStats?.monthly;
  Object.assign(p, {
    uid: p.uid || u.userId, wallet: p.wallet || u.wallet, evmWallet: u.evmWallet, x: p.x || u.x,
    followers: u.followers, banned: u.banned,
    pumpCallouts: cs?.n ?? null, pumpCallout2x: cs?.n ? cs.x2 / 100 : null, pumpCalloutAvgMult: cs?.avg ?? null, pumpCalloutMedMult: cs?.med ?? null,
    pumpCalloutPeakMin: cs?.peakMs ? cs.peakMs / 60e3 : null,
    portfolioPnlUsd: u.portfolio?.totalPnlUsd ?? p.pnlTotalUsd ?? null, portfolioValueUsd: u.portfolio?.valueUsd ?? null,
    ...(u.pnl?.monthly ? { pnlRank_monthly: u.pnl.monthly.rank, pnlUsd_monthly: u.pnl.monthly.pnlUsd, pnlPct_monthly: u.pnl.monthly.pnlPct } : {}),
    ...(u.pnl?.weekly ? { pnlRank_weekly: u.pnl.weekly.rank, pnlUsd_weekly: u.pnl.weekly.pnlUsd } : {}),
    ...(u.pnl?.daily ? { pnlRank_daily: u.pnl.daily.rank, pnlUsd_daily: u.pnl.daily.pnlUsd } : {}),
  });
}
for (const period of ['daily', 'weekly', 'monthly']) {
  const f = `${D}pump_pnl_${period}.json`;
  if (!fs.existsSync(f)) continue;
  for (const e of JSON.parse(fs.readFileSync(f))) {
    const h = wallets[e.wallet]; if (!h) continue;
    Object.assign(P('pump', h), { [`pnlRank_${period}`]: e.rank, [`pnlUsd_${period}`]: e.pnlUsd });
  }
}
fs.writeFileSync(D + 'profiles.json', JSON.stringify(Object.values(profiles)));
console.log('profiles', Object.keys(profiles).length);
