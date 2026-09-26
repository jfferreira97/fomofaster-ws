// Public pump.fun profile + stats for EVERY tracked Pump trader (not just the ones who posted
// callouts): profile (wallets, followers, X), pump's own callout stats, PnL leaderboard
// position per wallet, and portfolio PnL summary. Resumable, one file per handle.
const fs = require('fs');
const D = __dirname + '/data/';
const OUT = D + 'pump_users/';
fs.mkdirSync(OUT, { recursive: true });
const B = 'https://frontend-api-v3.pump.fun';
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function get(path) {
  for (let i = 0; i < 6; i++) {
    try {
      const r = await fetch(B + path, { headers: { 'User-Agent': 'Mozilla/5.0', Accept: 'application/json' } });
      if (r.ok) return await r.json();
      if ([400, 401, 403, 404].includes(r.status)) return { err: r.status };
      await sleep(r.status === 429 ? 4000 * (i + 1) : 400 * (i + 1));
    } catch { await sleep(1000 * (i + 1)); }
  }
  return { err: 'retries' };
}
const pick = s => s && !s.err ? {
  n: s.totalCallouts, x2: s.twoXPercent, x15: s.onePointFiveXPercent, avg: s.averageMultiple, med: s.medianMultiple, peakMs: s.averageTimeToPeakMs,
} : null;

(async () => {
  const traders = JSON.parse(fs.readFileSync(D + 'traders.json')).filter(t => t.pl === 'Pump');
  const todo = traders.filter(t => !fs.existsSync(OUT + encodeURIComponent(t.h) + '.json'));
  console.log('pump traders', traders.length, 'todo', todo.length);
  let i = 0, done = 0;
  await Promise.all(Array.from({ length: 5 }, async () => {
    while (i < todo.length) {
      const t = todo[i++];
      const u = await get(`/users/${encodeURIComponent(t.h)}`);
      const rec = { h: t.h, found: !u.err };
      if (!u.err) {
        const wallet = u.canonical_svm_wallet || u.address;
        const [cs, rank, pf] = await Promise.all([
          get(`/users/${wallet}/callout-stats`),
          get(`/pnl-leaderboard/projected-rank?wallet=${wallet}`),
          get(`/user-portfolio/${u.userId}`),
        ]);
        const pr = per => rank?.[per]?.found ? { rank: rank[per].projectedRank, pnlUsd: rank[per].entry?.pnlUsd, pnlPct: rank[per].entry?.pnlPercent, realizedUsd: rank[per].entry?.realizedPnlUsd } : null;
        const pos = (pf.positions || []).filter(p => !p.potentialSpam);
        Object.assign(rec, {
          userId: u.userId, username: u.username, wallet, evmWallet: u.canonical_evm_wallet || null, x: u.x_username || null,
          followers: u.followers ?? null, following: u.following ?? null, banned: !!u.is_banned, kind: u.kind,
          calloutStats: cs.err ? null : { daily: pick(cs.daily), weekly: pick(cs.weekly), monthly: pick(cs.monthly), all: pick(cs.allTime || cs.all_time || cs.lifetime) , raw: Object.keys(cs) },
          pnl: { daily: pr('daily'), weekly: pr('weekly'), monthly: pr('monthly') },
          portfolio: pf.summary ? { totalPnlUsd: pf.summary.totalPnlUsd, totalPnlPct: pf.summary.totalPnlPercentage, valueUsd: pf.summary.totalValueUsd, positions: pf.summary.positionCount,
            exited: pos.filter(p => p.isExited).length, exitedWins: pos.filter(p => p.isExited && p.realizedPnlUsd > 0).length,
            realizedUsd: pos.reduce((s, p) => s + (p.realizedPnlUsd || 0), 0) } : null,
        });
      }
      fs.writeFileSync(OUT + encodeURIComponent(t.h) + '.json', JSON.stringify(rec));
      if (++done % 50 === 0) console.log('done', done, '/', todo.length);
    }
  }));
  const all = fs.readdirSync(OUT).map(f => JSON.parse(fs.readFileSync(OUT + f)));
  console.log('found', all.filter(x => x.found).length, 'of', all.length, 'with callout stats', all.filter(x => x.calloutStats).length,
    'with 30d pnl', all.filter(x => x.pnl?.monthly).length, 'keys', JSON.stringify(all.find(x => x.calloutStats)?.calloutStats?.raw));
})();
