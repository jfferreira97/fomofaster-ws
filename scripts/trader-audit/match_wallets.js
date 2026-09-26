// Recovers each FOMO trader's on-chain wallet by matching their captured buys (token, time,
// USD size) against pump.fun's public per-token trade tape. A wallet that matches 2+
// independent buys of the same trader is taken as theirs.
// usage: node match_wallets.js [--only handle1,handle2] [--per 6]
const fs = require('fs');
const D = __dirname + '/data/';
const args = process.argv.slice(2);
const arg = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const ONLY = arg('--only', null)?.split(',');
const PER = +arg('--per', 6);
const OUT = D + 'fomo_wallets.json';
const caip = n => n === 1399811149 ? 'solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp' : `eip155:${n}`;
const sleep = ms => new Promise(r => setTimeout(r, ms));
async function get(url) {
  for (let i = 0; i < 4; i++) {
    try {
      const r = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0', Accept: 'application/json' } });
      if (r.ok) return await r.json();
      if ([400, 401, 404].includes(r.status)) return { err: r.status, body: (await r.text()).slice(0, 300) };
      await sleep(r.status === 429 ? 4000 * (i + 1) : 300 * (i + 1));
    } catch { await sleep(1000 * (i + 1)); }
  }
  return { err: 'retries' };
}

const tsOf = s => Date.parse(s.endsWith('Z') ? s : s.replace(' ', 'T') + 'Z');
const trades = JSON.parse(fs.readFileSync(D + 'fomo_trades.json'));
const recentCut = require('./window').END - (args.includes('--retry-all') ? 45 : 15) * 864e5; // pump's trade tape is freshest for recent swaps
const buysBy = {};
for (const t of trades) {
  if (t.t !== 'swap_buy' || !(t.u >= 25) || ![1399811149, 4663, 56, 8453].includes(t.n)) continue;
  const at = tsOf(t.c); if (at < recentCut) continue;
  (buysBy[t.h] ||= []).push({ ...t, at });
}
const result = fs.existsSync(OUT) ? JSON.parse(fs.readFileSync(OUT)) : {};
// --retry-unmatched: give traders with only a single matching fill another, deeper pass
if (args.includes('--retry-all')) for (const [h, r] of Object.entries(result)) if (!r.sol && !r.evm) delete result[h];
if (args.includes('--retry-unmatched')) for (const [h, r] of Object.entries(result)) if (!r.sol && !r.evm && (r.solVotes || r.evmVotes)) delete result[h];

async function matchOne(b) {
  // page backward from just after the buy; stop once past it
  const q = new URLSearchParams({ limit: '100', minUsd: String(Math.floor(b.u * 0.85)) });
  let url = `https://frontend-api-v3.pump.fun/trades/${caip(b.n)}/${b.a}?${q}&before=0-0-0-${b.at + 60e3}`;
  let r = await get(url);
  if (r.err === 400) { r = await get(`https://frontend-api-v3.pump.fun/trades/${caip(b.n)}/${b.a}?${q}`); } // before format rejected
  if (!r.trades) return { err: r.err, body: r.body };
  const hits = r.trades.filter(x => x.side === 'buy' && x.trader?.address && Math.abs(x.blockTimeMs - b.at) < 20e3
    && Math.abs(+x.valueUsd / b.u - 1) < 0.12);
  return { wallets: [...new Set(hits.map(x => x.trader.address))], seen: r.trades.length,
    span: r.trades.length ? [r.trades.at(-1).blockTimeMs - b.at, r.trades[0].blockTimeMs - b.at] : null };
}

(async () => {
  const handles = ONLY || Object.keys(buysBy);
  let done = 0, i = 0;
  const todo = handles.filter(h => !result[h] && buysBy[h]);
  await Promise.all(Array.from({ length: 10 }, async () => {
    while (i < todo.length) {
      const h = todo[i++];
      // spread picks across tokens; bigger buys are easier to pin down
      const seen = new Set(), picks = [];
      for (const b of [...buysBy[h]].sort((x, y) => y.u - x.u)) { if (seen.has(b.a)) continue; seen.add(b.a); picks.push(b); if (picks.length >= PER) break; }
      const votes = {}, byChain = {}, log = [];
      for (const b of picks) {
        const m = await matchOne(b);
        log.push({ k: b.k, n: b.n, u: Math.round(b.u), ...m });
        for (const w of m.wallets || []) { votes[w] = (votes[w] || 0) + 1; byChain[w] = b.n; }
      }
      const ranked = Object.entries(votes).sort((a, b) => b[1] - a[1]);
      const sol = ranked.find(([w]) => byChain[w] === 1399811149), evm = ranked.find(([w]) => byChain[w] !== 1399811149);
      result[h] = { sol: sol && sol[1] >= 2 ? sol[0] : null, solVotes: sol?.[1] || 0, evm: evm && evm[1] >= 2 ? evm[0] : null, evmVotes: evm?.[1] || 0, tried: picks.length, log: ONLY ? log : undefined };
      if (++done % 20 === 0) { fs.writeFileSync(OUT, JSON.stringify(result)); console.log('traders', done, '/', todo.length); }
    }
  }));
  fs.writeFileSync(OUT, JSON.stringify(result));
  const v = Object.values(result);
  console.log('done', v.length, 'sol wallets', v.filter(x => x.sol).length, 'evm wallets', v.filter(x => x.evm).length);
  if (ONLY) console.log(JSON.stringify(ONLY.map(h => [h, result[h]]), null, 1).slice(0, 4000));
})();
