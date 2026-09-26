// Fair platform comparison for the public post: a trader's FIRST FOMO thesis on a token vs
// a trader's FIRST pump.fun callout on a token (both are "here's my call" posts), scored the
// same way at two horizons. Same entry rule for both: the first 1m candle after the post.
//   short = 1 hour  (exit: +50% take-profit / -30% stop / out at 1h)
//   mid   = 24 hours (exit: 3x take-profit / -40% stop / out at 24h)
// Controls: market-cap bands, Solana-only, and each half of the window separately.
const fs = require('fs');
const L = require('./lib');
const W = require('./window');
const barsFor = L.loadBars();
const inWin = t => t >= W.SINCE && t < W.END;

function firstPer(rows) { // first post per trader per token
  const seen = new Set(), out = [];
  for (const r of rows.sort((a, b) => a.t - b.t)) { const k = `${r.h}|${r.n}|${r.a}`; if (seen.has(k)) continue; seen.add(k); out.push(r); }
  return out;
}
const thesis = firstPer(JSON.parse(fs.readFileSync(L.D + 'fomo_thesis.json')).filter(t => t.c && t.a)
  .map(t => ({ pl: 'FOMO thesis', h: t.h, n: t.n, a: t.a, t: L.tsOf(t.c), m: +t.m || null })).filter(r => inWin(r.t)));
const callouts = firstPer(JSON.parse(fs.readFileSync(L.D + 'pump_callouts.json')).filter(c => c.a && !c.q)
  .map(c => ({ pl: 'Pump callout', h: c.h, n: c.n, a: c.a, t: L.tsOf(c.c), m: +c.m || null })).filter(r => inWin(r.t)));
console.log('first posts per trader+token:', thesis.length, 'FOMO theses,', callouts.length, 'pump callouts');

// Thesis payloads carry no market cap. Every stored trade/callout has price AND mcap, so
// mcap / price = the token's supply; supply x the price at the post = the mcap at the post.
const supply = new Map();
{
  const push = (n, a, m, p) => { if (m > 0 && p > 0) { const k = `${n}_${a}`; (supply.get(k) || supply.set(k, []).get(k)).push(m / p); } };
  for (const t of JSON.parse(fs.readFileSync(L.D + 'fomo_trades.json'))) push(t.n, t.a, t.m, t.p);
  for (const c of JSON.parse(fs.readFileSync(L.D + 'pump_callouts.json'))) push(c.n, c.a, +c.m, +c.p);
  for (const [k, v] of supply) supply.set(k, L.med(v));
}

function score(r) {
  const p = L.makePath(barsFor(r.n, r.a, r.t), r.t);
  if (!p) return null;
  if (r.m == null) { const s = supply.get(`${r.n}_${r.a}`); if (s) { r.m = s * p.E; r.mDerived = true; } }
  const at = min => { const i = L.lastIdxBefore(p.T, min); return (i >= 0 ? p.C[i] : 1) - 1; };
  const peak = min => { const i = L.lastIdxBefore(p.T, min); return i >= 0 ? p.MX[i] : 1; };
  const low = min => { const i = L.lastIdxBefore(p.T, min); return i >= 0 ? p.MN[i] : 1; };
  return {
    short: { ev: L.exitReturn(p, 0.5, 0.3, 60), ret: at(60), peak: peak(60), dump30: low(60) <= 0.7, up50: peak(60) >= 1.5 },
    mid: { ev: L.exitReturn(p, 2, 0.4, 1440), ret: at(1440), peak: peak(1440), dump30: low(1440) <= 0.7, x2: peak(1440) >= 2 },
  };
}
for (const r of [...thesis, ...callouts]) r.s = score(r);
console.log("thesis mcap known:", thesis.filter(r => r.s && r.m != null).length, "of", thesis.filter(r => r.s).length, "scored (derived:", thesis.filter(r => r.mDerived).length + ")");

const pct = (a, f) => a.length ? a.filter(f).length / a.length : null;
function sum(rows) {
  const s = rows.filter(r => r.s);
  if (!s.length) return { n: 0 };
  const S = s.map(r => r.s.short), M = s.map(r => r.s.mid);
  return {
    n: s.length, posts: rows.length, traders: new Set(s.map(r => r.h)).size, mcapMed: L.med(s.map(r => r.m).filter(Boolean)),
    short: { ev: L.mean(S.map(x => x.ev)), retMed: L.med(S.map(x => x.ret)), up50: pct(S, x => x.up50), dump30: pct(S, x => x.dump30), green: pct(S, x => x.ret > 0) },
    mid: { ev: L.mean(M.map(x => x.ev)), retMed: L.med(M.map(x => x.ret)), x2: pct(M, x => x.x2), dump30: pct(M, x => x.dump30), green: pct(M, x => x.ret > 0) },
  };
}
const both = f => ({ fomo: sum(thesis.filter(f)), pump: sum(callouts.filter(f)) });
const bands = [[0, 100e3, 'Under $100K'], [100e3, 1e6, '$100K – $1M'], [1e6, Infinity, '$1M+']];
const out = {
  window: W.label, halfA: W.halfA, halfB: W.halfB,
  overall: both(() => true),
  solana: both(r => r.n === 1399811149),
  bands: bands.map(([lo, hi, label]) => ({ label, ...both(r => r.m != null && r.m >= lo && r.m < hi) })),
  halves: [{ label: W.halfA, ...both(r => r.t < W.SPLIT) }, { label: W.halfB, ...both(r => r.t >= W.SPLIT) }],
};
// ---- the "which alerts are worth sending" view: same 3-exit average the alert scorecard uses,
// for all traders and for kept (not cut) traders, plus how each decays with alert delay ----
const intel = JSON.parse(fs.readFileSync(L.D + 'intel.json'));
const style = new Map(intel.traders.map(t => [t.key, t.style]));
const kept = (r, pl) => { const s = style.get(`${pl}|${r.h}`); return s && s !== 'noise' && s !== 'unrated'; };
const ev3 = p => (L.exitReturn(p, 0.5, 0.3, 60) + L.exitReturn(p, 1, 0.3, 240) + L.exitReturn(p, 2, 0.4, 1440)) / 3;
const DELAYS = [1, 2, 3, 5, 10, 15, 30];
function decay(rows) {
  return DELAYS.map(d => {
    const r = [];
    for (const x of rows) { const p = L.makePath(barsFor(x.n, x.a, x.t), x.t + (d - 1) * 60e3); if (p) r.push(ev3(p)); }
    return { delay: d, n: r.length, ev: L.mean(r) };
  });
}
const scored = rows => rows.filter(r => r.s);
const cards = {};
for (const [key, rows, pl] of [['thesis', thesis, 'fomo'], ['callout', callouts, 'pump']]) {
  const all = scored(rows), k = all.filter(r => kept(r, pl));
  const avg3 = rs => L.mean(rs.map(r => { const p = L.makePath(barsFor(r.n, r.a, r.t), r.t); return p ? ev3(p) : null; }).filter(v => v != null));
  cards[key] = {
    all: { n: all.length, ev3: avg3(all), short: L.mean(all.map(r => r.s.short.ev)), mid: L.mean(all.map(r => r.s.mid.ev)) },
    kept: { n: k.length, ev3: avg3(k), short: L.mean(k.map(r => r.s.short.ev)), mid: L.mean(k.map(r => r.s.mid.ev)) },
    decayAll: decay(all), decayKept: decay(k),
  };
  console.log(key, 'all', cards[key].all, 'kept', cards[key].kept);
}
out.cards = cards;
fs.writeFileSync(L.D + 'research_platform.json', JSON.stringify(out));

const f = v => v == null ? '  –  ' : ((v >= 0 ? '+' : '') + (v * 100).toFixed(1) + '%').padStart(7);
const p0 = v => v == null ? ' – ' : (Math.round(v * 100) + '%').padStart(4);
const line = (lab, x) => x.n ? console.log(`  ${lab.padEnd(13)} n=${String(x.n).padStart(5)} mcap~$${Math.round((x.mcapMed || 0) / 1e3)}K | 1h: avg ${f(x.short.ev)} med ${f(x.short.retMed)} +50% ${p0(x.short.up50)} -30% ${p0(x.short.dump30)} | 24h: avg ${f(x.mid.ev)} med ${f(x.mid.retMed)} 2x ${p0(x.mid.x2)} -30% ${p0(x.mid.dump30)}`) : console.log(`  ${lab}: no data`);
for (const [name, g] of [['OVERALL', out.overall], ['SOLANA ONLY', out.solana], ...out.bands.map(b => [b.label, b]), ...out.halves.map(h => ['half ' + h.label, h])]) {
  console.log('\n== ' + name); line('FOMO thesis', g.fomo); line('Pump callout', g.pump);
}
