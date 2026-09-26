// For every FOMO trader whose Solana wallet we matched, pull pump.fun's public per-wallet
// PnL leaderboard position (daily / weekly / monthly: rank, PnL USD, PnL %, spend).
const fs = require('fs');
const D = __dirname + '/data/';
const W = JSON.parse(fs.readFileSync(D + 'fomo_wallets.json'));
const OUT = D + 'fomo_wallet_pnl.json';
const out = fs.existsSync(OUT) ? JSON.parse(fs.readFileSync(OUT)) : {};
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function get(url) {
  for (let i = 0; i < 8; i++) {
    try {
      const r = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0', Accept: 'application/json' } });
      if (r.ok) return await r.json();
      if ([400, 401, 404].includes(r.status)) return { err: r.status };
      await sleep(r.status === 429 ? 4000 * (i + 1) : 400 * (i + 1));
    } catch { await sleep(1000 * (i + 1)); }
  }
  return { err: 'retries' };
}
(async () => {
  const todo = Object.entries(W).filter(([h, w]) => w.sol && !out[h]);
  let i = 0;
  await Promise.all(Array.from({ length: 3 }, async () => {
    while (i < todo.length) {
      const [h, w] = todo[i++];
      const r = await get(`https://frontend-api-v3.pump.fun/pnl-leaderboard/projected-rank?wallet=${w.sol}`);
      const pick = per => r?.[per]?.found ? { rank: r[per].projectedRank, pnlUsd: r[per].entry?.pnlUsd, pnlPct: r[per].entry?.pnlPercent,
        spendSol: r[per].entry?.buySpendSol, realizedUsd: r[per].entry?.realizedPnlUsd } : null;
      out[h] = { wallet: w.sol, daily: pick('daily'), weekly: pick('weekly'), monthly: pick('monthly'), err: r.err };
    }
  }));
  fs.writeFileSync(OUT, JSON.stringify(out));
  console.log('wallet pnl', Object.keys(out).length, 'with monthly', Object.values(out).filter(x => x.monthly).length);
})();
