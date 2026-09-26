// Fetches 1m candles covering [call-5m, call+24h] for every call in the window, via pump.fun's
// multi-chain candle route. Resumable: one file per token-group in data/candles/.
// usage: node fetch_candles.js [--sample N] [--conc C] [--chain ID]
const fs = require('fs');
const D = __dirname + '/data/';
const OUT = D + 'candles/';
fs.mkdirSync(OUT, { recursive: true });
const args = process.argv.slice(2);
const arg = (k, d) => { const i = args.indexOf(k); return i >= 0 ? args[i + 1] : d; };
const SAMPLE = +arg('--sample', 0), CONC = +arg('--conc', 6), CHAIN = arg('--chain', null);
const SINCE = require('./window').SINCE;
const H24 = 24 * 3600e3, PRE = 5 * 60e3;

const caip = n => n === 1399811149 ? 'solana:5eykt4UsFv8P8NJdTREpY1vzqKqZKvdp' : `eip155:${n}`;
const SERVED = new Set([1399811149, 4663, 56, 8453, 1, 5042]);

const calls = JSON.parse(fs.readFileSync(D + 'calls.json')).filter(c => c.t >= SINCE && SERVED.has(c.n) && (!CHAIN || c.n == CHAIN));
// group calls per token into windows: calls within 24h of the group start share one fetch
const byTok = new Map();
for (const c of calls) { const k = `${c.n}_${c.a}`; (byTok.get(k) || byTok.set(k, []).get(k)).push(c.t); }
let jobs = [];
for (const [k, tsArr] of byTok) {
  tsArr.sort((a, b) => a - b);
  let g = null;
  for (const t of tsArr) {
    if (!g || t - g.from > H24) { g = { k, from: t, to: t }; jobs.push(g); } else g.to = t;
  }
}
if (SAMPLE) { // stratified sample per chain
  const per = {}; jobs = jobs.sort(() => Math.random() - .5).filter(j => { const n = j.k.split('_')[0]; per[n] = (per[n] || 0) + 1; return per[n] <= SAMPLE; });
}
const file = j => `${OUT}${j.k}_${j.from}.json`;
const todo = jobs.filter(j => !fs.existsSync(file(j)) || JSON.parse(fs.readFileSync(file(j))).err === 'retries');
console.log(`groups ${jobs.length}, todo ${todo.length}`);

const sleep = ms => new Promise(r => setTimeout(r, ms));
async function get(url) {
  for (let i = 0; i < 10; i++) {
    try {
      const r = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0', Accept: 'application/json' } });
      if (r.status === 200) return await r.json();
      if (r.status === 429) console.log("429");
      if (r.status === 400 || r.status === 404) return { err: r.status, body: (await r.text()).slice(0, 200) };
      // 503s are sporadic lane hiccups — retry quickly; 429 means actually slow down
      await sleep(r.status === 429 ? 5000 * (i + 1) : 300 * (i + 1) + Math.random() * 300);
    } catch { await sleep(1000 * (i + 1)); }
  }
  return { err: 'retries' };
}

async function run(j) {
  const [n, a] = j.k.split('_');
  const start = j.from - PRE, end = j.to + H24;
  let to = Math.min(end, Date.now()), bars = [], err = null, source = null;
  for (let page = 0; page < 6; page++) {
    const r = await get(`https://frontend-api-v3.pump.fun/candles/${caip(+n)}/${a}?res=1m&count=1000&to=${to}`);
    if (!r.candles) { err = r.err || 'bad'; break; }
    source = r.source;
    const got = r.candles.map(b => [b.openTimeMs, +b.open, +b.high, +b.low, +b.close, +b.volumeUsd]);
    bars = got.concat(bars);
    if (!got.length || !r.hasOlder || got[0][0] <= start) break;
    to = got[0][0] - 1;
  }
  bars = bars.filter(b => b[0] >= start - 60e3 && b[0] <= end);
  fs.writeFileSync(file(j), JSON.stringify({ k: j.k, from: j.from, to: j.to, err, source, bars }));
  return bars.length;
}

(async () => {
  let done = 0, empty = 0, i = 0; const t0 = Date.now();
  const worker = async () => { while (i < todo.length) { const j = todo[i++]; const n = await run(j); done++; if (!n) empty++;
    if (done % 200 === 0) console.log(`${done}/${todo.length} empty=${empty} ${((Date.now() - t0) / done).toFixed(0)}ms/job`); } };
  await Promise.all(Array.from({ length: CONC }, worker));
  console.log(`done ${done} empty ${empty} in ${((Date.now() - t0) / 1000).toFixed(0)}s`);
})();
