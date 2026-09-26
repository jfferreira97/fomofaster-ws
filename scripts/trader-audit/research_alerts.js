// Which ALERTS are actionable? Scores every alert type the bot sends by what happened to the
// price after it — entering ~1 minute late, averaged over the three exit styles (scalp / flip /
// runner, 3% costs) — and tests the knobs the app already has (per-trader $ floor, chain
// min-mcap, Trending threshold, repeat window) plus sell alerts and latency.
const fs = require('fs');
const L = require('./lib');
const W = require('./window');
const intel = JSON.parse(fs.readFileSync(L.D + 'intel.json'));
const tInfo = new Map(intel.traders.map(t => [t.key, t]));
const barsFor = L.loadBars();
const inWin = t => t >= W.SINCE && t < W.END;
const half = t => t < W.SPLIT ? 'A' : 'B';

// follower outcome at time t: avg of the three exits, plus 2x-in-24h and 1h/4h drift
function outcome(n, a, t, delayMin = 1) {
  const p = L.makePath(barsFor(n, a, t), t + (delayMin - 1) * 60e3);
  if (!p) return null;
  const ev = (L.exitReturn(p, 0.5, 0.3, 60) + L.exitReturn(p, 1, 0.3, 240) + L.exitReturn(p, 2, 0.4, 1440)) / 3;
  const end1h = L.lastIdxBefore(p.T, 60), end4h = L.lastIdxBefore(p.T, 240);
  return { ev, x2: p.MX[p.MX.length - 1] >= 2, r1h: (end1h >= 0 ? p.C[end1h] : 1) - 1, r4h: (end4h >= 0 ? p.C[end4h] : 1) - 1,
    dd30: L.firstIdx(p.MN, 0.7, false) <= L.lastIdxBefore(p.T, 240) };
}
function summarize(name, rows) {
  const o = rows.filter(r => r.o);
  if (!o.length) return { name, n: 0 };
  const s = { name, n: o.length, ev: L.mean(o.map(r => r.o.ev)), x2: o.filter(r => r.o.x2).length / o.length,
    r1h: L.med(o.map(r => r.o.r1h)), r4h: L.med(o.map(r => r.o.r4h)), dd30: o.filter(r => r.o.dd30).length / o.length,
    evA: L.mean(o.filter(r => r.half === 'A').map(r => r.o.ev)), evB: L.mean(o.filter(r => r.half === 'B').map(r => r.o.ev)) };
  const f = v => v == null || isNaN(v) ? '  –  ' : ((v >= 0 ? '+' : '') + (v * 100).toFixed(1) + '%').padStart(6);
  console.log(`  ${name.padEnd(34)} n=${String(s.n).padStart(6)}  avg ${f(s.ev)} (A ${f(s.evA)} B ${f(s.evB)})  2x ${(s.x2 * 100).toFixed(0).padStart(2)}%  med1h ${f(s.r1h)}  med4h ${f(s.r4h)}`);
  return s;
}
const out = {};
const section = (k, title, groups) => { console.log(`\n== ${title}`); out[k] = groups.map(([name, rows]) => summarize(name, rows)); };

// ---------- FOMO alerts: first buys, adds, sells (with position state) ----------
const trades = JSON.parse(fs.readFileSync(L.D + 'fomo_trades.json'));
const pos = new Map();
const fomo = [];
const medBuy = {};
for (const t of trades) if (t.t === 'swap_buy' && t.u > 0) (medBuy[t.h] ||= []).push(t.u);
for (const h in medBuy) medBuy[h] = L.med(medBuy[h]);
for (const t of trades) {
  if (!(t.p > 0) || !t.a) continue;
  const time = L.tsOf(t.c), k = `${t.h}|${t.n}|${t.a}`, qty = (t.u || 0) / t.p;
  let s = pos.get(k);
  let kind;
  if (t.t === 'swap_buy') {
    if (!s || s.q <= s.peak * 0.05) { s = { q: 0, peak: 0, t0: time }; pos.set(k, s); kind = 'first'; } else kind = 'add';
    s.q += qty; s.peak = Math.max(s.peak, s.q);
  } else if (t.t === 'swap_sell') {
    if (!s) continue;
    const frac = s.q > 0 ? Math.min(1, qty / s.q) : 1;
    s.q = Math.max(0, s.q - qty);
    kind = s.q <= s.peak * 0.05 ? 'sell_full' : frac >= 0.5 ? 'sell_big' : 'sell_part';
  } else continue;
  if (!inWin(time)) continue;
  const ti = tInfo.get(`fomo|${t.h}`);
  fomo.push({ kind, h: t.h, n: t.n, a: t.a, t: time, u: t.u, m: t.m, half: half(time), style: ti?.style || 'unrated', cut: ti?.style === 'noise',
    rel: medBuy[t.h] ? t.u / medBuy[t.h] : null, holdMin: s ? (time - s.t0) / 60e3 : null });
}
console.log('fomo alerts in window', fomo.length);
let i = 0;
for (const r of fomo) { r.o = outcome(r.n, r.a, r.t); if (++i % 20000 === 0) console.log('  scored', i); }

const kept = r => !r.cut && r.style !== 'unrated';
section('fomoTypes', 'FOMO alert types (all traders)', [
  ['first buy', fomo.filter(r => r.kind === 'first')], ['add (repeat buy)', fomo.filter(r => r.kind === 'add')],
  ['sell: partial (<50% of bag)', fomo.filter(r => r.kind === 'sell_part')], ['sell: big (50%+)', fomo.filter(r => r.kind === 'sell_big')], ['sell: full exit', fomo.filter(r => r.kind === 'sell_full')],
]);
section('fomoTypesKept', 'FOMO alert types (kept traders only)', [
  ['first buy', fomo.filter(r => r.kind === 'first' && kept(r))], ['add (repeat buy)', fomo.filter(r => r.kind === 'add' && kept(r))],
  ['sell: partial', fomo.filter(r => r.kind === 'sell_part' && kept(r))], ['sell: big', fomo.filter(r => r.kind === 'sell_big' && kept(r))], ['sell: full exit', fomo.filter(r => r.kind === 'sell_full' && kept(r))],
]);
// what happens to price after a sell, for someone already holding (did selling with them help?)
section('afterSell', 'After a sell alert: price drift for holders (negative = selling with them was right)', [
  ['sell: partial', fomo.filter(r => r.kind === 'sell_part')], ['sell: big', fomo.filter(r => r.kind === 'sell_big')], ['sell: full exit', fomo.filter(r => r.kind === 'sell_full')],
  ['full exit within 30m of entry', fomo.filter(r => r.kind === 'sell_full' && r.holdMin != null && r.holdMin <= 30)],
]);

// buy size: absolute and relative to the trader's usual size (conviction)
const firstKept = fomo.filter(r => r.kind === 'first' && kept(r));
section('buySize', 'First buys by USD size (kept traders)', [
  ['< $100', firstKept.filter(r => r.u < 100)], ['$100–500', firstKept.filter(r => r.u >= 100 && r.u < 500)], ['$500–2K', firstKept.filter(r => r.u >= 500 && r.u < 2000)],
  ['$2K–10K', firstKept.filter(r => r.u >= 2000 && r.u < 10000)], ['$10K+', firstKept.filter(r => r.u >= 10000)],
]);
section('buyRel', 'First buys by size vs the trader\'s usual buy (conviction)', [
  ['< 0.5x usual', firstKept.filter(r => r.rel != null && r.rel < 0.5)], ['0.5–1.5x usual', firstKept.filter(r => r.rel >= 0.5 && r.rel < 1.5)],
  ['1.5–3x usual', firstKept.filter(r => r.rel >= 1.5 && r.rel < 3)], ['3x+ usual', firstKept.filter(r => r.rel >= 3)],
]);

// ---------- Pump callouts ----------
const pc = JSON.parse(fs.readFileSync(L.D + 'pump_callouts.json')).map(c => ({ ...c, t: L.tsOf(c.c) })).filter(c => inWin(c.t) && +c.p > 0);
const pump = pc.map(c => { const ti = tInfo.get(`pump|${c.h}`); return { kind: c.q ? 'quote' : 'callout', h: c.h, n: c.n, a: c.a, t: c.t, m: +c.m, half: half(c.t),
  style: ti?.style || 'unrated', cut: ti?.style === 'noise', o: outcome(c.n, c.a, c.t) }; });
section('pumpTypes', 'Pump callouts', [
  ['callout (all)', pump.filter(r => r.kind === 'callout')], ['quote-callout (all)', pump.filter(r => r.kind === 'quote')],
  ['callout (kept traders)', pump.filter(r => r.kind === 'callout' && kept(r))], ['callout (cut traders)', pump.filter(r => r.kind === 'callout' && r.cut)],
]);

// ---------- market cap at the alert (chain min-mcap setting) ----------
const entries = [...firstKept.map(r => ({ ...r, pl: 'fomo' })), ...pump.filter(r => r.kind === 'callout' && kept(r)).map(r => ({ ...r, pl: 'pump' }))];
const capB = [[0, 20e3, '< $20K'], [20e3, 50e3, '$20–50K'], [50e3, 100e3, '$50–100K'], [100e3, 300e3, '$100–300K'], [300e3, 1e6, '$300K–1M'], [1e6, 10e6, '$1–10M'], [10e6, Infinity, '$10M+']];
section('mcap', 'Entries by market cap at alert (kept traders, both platforms)', capB.map(([lo, hi, l]) => [l, entries.filter(r => r.m >= lo && r.m < hi)]));
const NET = { 1399811149: 'SOL', 4663: 'ROBINHOOD', 56: 'BNB', 8453: 'BASE', 1: 'ETH', 5042: 'ARC' };
section('chain', 'Entries by chain (kept traders)', Object.entries(NET).map(([n, l]) => [l, entries.filter(r => r.n == n)]));
// the same mcap floors per chain, as the chain min-mcap setting would apply them
out.floors = {};
for (const [n, l] of Object.entries(NET)) {
  const e = entries.filter(r => r.n == n); if (e.length < 300) continue;
  console.log(`\n== ${l}: min-mcap floor`);
  out.floors[l] = [0, 20e3, 50e3, 100e3, 250e3, 500e3, 1e6].map(f => summarize(`floor $${f >= 1e6 ? f / 1e6 + 'M' : f / 1e3 + 'K'} (keeps ${Math.round(100 * e.filter(r => r.m >= f).length / e.length)}%)`, e.filter(r => r.m >= f)));
}

// ---------- confluence: how many distinct traders touched the token in the 30 min before ----------
const touches = new Map(); // token -> [{t, key}]
for (const r of fomo.filter(r => r.kind === 'first')) (touches.get(`${r.n}_${r.a}`) || touches.set(`${r.n}_${r.a}`, []).get(`${r.n}_${r.a}`)).push({ t: r.t, key: 'fomo|' + r.h, kept: kept(r) });
for (const r of pump.filter(r => r.kind === 'callout')) (touches.get(`${r.n}_${r.a}`) || touches.set(`${r.n}_${r.a}`, []).get(`${r.n}_${r.a}`)).push({ t: r.t, key: 'pump|' + r.h, kept: kept(r) });
for (const v of touches.values()) v.sort((a, b) => a.t - b.t);
const conf = (r, pl, keptOnly) => new Set((touches.get(`${r.n}_${r.a}`) || []).filter(x => x.t <= r.t && x.t > r.t - 30 * 60e3 && (!keptOnly || x.kept)).map(x => x.key)).size;
const allEntries = [...fomo.filter(r => r.kind === 'first').map(r => ({ ...r, pl: 'fomo' })), ...pump.filter(r => r.kind === 'callout').map(r => ({ ...r, pl: 'pump' }))];
for (const r of allEntries) { r.cAll = conf(r, r.pl, false); r.cKept = conf(r, r.pl, true); }
section('confAll', 'Entry alerts by # distinct traders on the token in the prior 30 min (anyone)', [1, 2, 3, 4, 5].map(k => [k === 5 ? '5+ traders' : `${k} trader${k > 1 ? 's' : ''}`, allEntries.filter(r => (k === 5 ? r.cAll >= 5 : r.cAll === k))]));
section('confKept', '...counting only kept (non-cut, rated) traders', [1, 2, 3, 4].map(k => [k === 4 ? '4+ kept traders' : `${k} kept trader${k > 1 ? 's' : ''}`, allEntries.filter(r => r.cKept >= 1 && (k === 4 ? r.cKept >= 4 : r.cKept === k))]));

// ---------- the Trending alerts the bot actually sent ----------
const tr = JSON.parse(fs.readFileSync(L.D + 'confluence.json')).map(c => ({ ...c, t: L.tsOf(c.f) })).filter(c => inWin(c.t));
for (const c of tr) { c.half = half(c.t); c.o = outcome(c.n, c.a, c.t); }
// first alert per token window vs re-alerts
const seen = new Set();
for (const c of tr) { const k = `${c.a}|${c.ws}`; c.first = !seen.has(k); seen.add(k); }
section('trending', 'Trending alerts sent (live config: 4+ traders / 30 min)', [
  ['all Trending alerts', tr], ['first alert per token window', tr.filter(c => c.first)], ['re-alerts (count went up)', tr.filter(c => !c.first)],
  ['4 traders', tr.filter(c => c.tc === 4)], ['5–6 traders', tr.filter(c => c.tc >= 5 && c.tc <= 6)], ['7–9 traders', tr.filter(c => c.tc >= 7 && c.tc <= 9)], ['10+ traders', tr.filter(c => c.tc >= 10)],
]);
out.trendingPerDay = tr.length / ((W.END - W.SINCE) / 864e5);

// ---------- latency (kept first buys + callouts) ----------
console.log('\n== Latency: same entry alerts, follower enters N minutes after the alert');
const keptEntries = allEntries.filter(r => !r.cut && r.style !== 'unrated');
out.latency = [1, 2, 3, 5, 10, 15, 30].map(d => { const rows = keptEntries.map(r => ({ half: r.half, o: outcome(r.n, r.a, r.t, d) })); return { delay: d, ...summarize(`${d} min late`, rows) }; });

fs.writeFileSync(L.D + 'research_alerts.json', JSON.stringify(out));
