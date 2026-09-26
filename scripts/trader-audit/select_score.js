// Compare trader-scoring rules out of sample, both directions (A→B and B→A).
// For each rule: pick top 20 / top 50 on one half, measure follower EV on the other half
// (each trader played with the exit style that was best for them in the selection half),
// and measure the bottom (cut candidates) the same way.
const fs = require('fs');
const D = __dirname + '/data/';
const calls = JSON.parse(fs.readFileSync(D + 'call_metrics.json')).filter(c => c.sim);
const SPLIT = require('./window').SPLIT;
const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null;
const med = a => { if (!a.length) return null; const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
const rate = (a, f) => a.length ? a.filter(f).length / a.length : null;
const strats = ['scalp', 'flip', 'runner'];

function stats(cs, popArr) {
  const by = {};
  for (const c of cs) (by[`${c.platform}|${c.h}`] ||= []).push(c);
  const pop = { ev: Object.fromEntries(strats.map(s => [s, mean(cs.map(c => c.sim[s]))])), h2: rate(cs, c => c.sim.pk24h >= 2), sl: rate(cs, c => c.sim.sl30) };
  const shrink = (v, n, p, k) => (v * n + p * k) / (n + k);
  const out = {};
  for (const [k, v] of Object.entries(by)) {
    if (v.length < 8) continue;
    const s = v.map(c => c.sim), n = v.length;
    const ev = Object.fromEntries(strats.map(st => [st, mean(s.map(x => x[st]))]));
    const best = strats.reduce((a, b) => ev[a] >= ev[b] ? a : b);
    out[k] = {
      n, best,
      evShr12: shrink(ev[best], n, pop.ev[best], 12),
      evAllShr30: mean(strats.map(st => shrink(ev[st], n, pop.ev[st], 30))),
      h2Shr: shrink(rate(s, x => x.pk24h >= 2), n, pop.h2, 20),
      r1h: med(s.map(x => x.r1h)),
      slShr: shrink(rate(s, x => x.sl30), n, pop.sl, 20),
      medScalp: med(s.map(x => x.scalp)),
    };
  }
  return out;
}
function zmap(obj, f) { const ks = Object.keys(obj), v = ks.map(k => f(obj[k])); const m = mean(v), sd = Math.sqrt(mean(v.map(x => (x - m) ** 2))) || 1; return Object.fromEntries(ks.map((k, i) => [k, (v[i] - m) / sd])); }
const RULES = {
  'past EV (shrunk k=12)': s => s.evShr12,
  'avg EV all styles (k=30)': s => s.evAllShr30,
  '2x hit rate (shrunk)': s => s.h2Shr,
  'composite': null, // z(2x) + z(r1h) - z(stop-out) + z(avg EV k=30)
};
function score(st, rule) {
  if (rule !== 'composite') return Object.fromEntries(Object.entries(st).map(([k, v]) => [k, RULES[rule](v)]));
  const a = zmap(st, s => s.h2Shr), b = zmap(st, s => s.r1h), c = zmap(st, s => s.slShr), d = zmap(st, s => s.evAllShr30);
  return Object.fromEntries(Object.keys(st).map(k => [k, a[k] + b[k] - c[k] + d[k]]));
}
function evalOn(keys, selStats, testCalls) {
  const byT = {};
  for (const c of testCalls) (byT[`${c.platform}|${c.h}`] ||= []).push(c);
  const r = [];
  for (const k of keys) for (const c of byT[k] || []) r.push(c.sim[selStats[k].best]);
  return { calls: r.length, ev: mean(r) };
}
const halves = { A: calls.filter(c => c.t < SPLIT), B: calls.filter(c => c.t >= SPLIT) };
const res = {};
for (const [sel, test] of [['A', 'B'], ['B', 'A']]) {
  const st = stats(halves[sel]);
  const all = evalOn(Object.keys(st), st, halves[test]);
  console.log(`\n${sel}→${test}: all rated traders ${(all.ev * 100).toFixed(1)}%/call (${all.calls} calls)`);
  for (const rule of Object.keys(RULES)) {
    const sc = score(st, rule);
    const ranked = Object.keys(sc).sort((a, b) => sc[b] - sc[a]);
    const t20 = evalOn(ranked.slice(0, 20), st, halves[test]), t50 = evalOn(ranked.slice(0, 50), st, halves[test]);
    const bot = evalOn(ranked.slice(-Math.round(ranked.length * 0.35)), st, halves[test]);
    const keep = evalOn(ranked.slice(0, ranked.length - Math.round(ranked.length * 0.35)), st, halves[test]);
    (res[rule] ||= {})[sel + test] = { t20: t20.ev, t50: t50.ev, bottom35: bot.ev, keep65: keep.ev, all: all.ev };
    console.log(`  ${rule.padEnd(26)} top20 ${(t20.ev * 100).toFixed(1).padStart(5)}%  top50 ${(t50.ev * 100).toFixed(1).padStart(5)}%  keep 65% ${(keep.ev * 100).toFixed(1).padStart(5)}%  cut 35% ${(bot.ev * 100).toFixed(1).padStart(5)}%`);
  }
}
fs.writeFileSync(D + 'score_validation.json', JSON.stringify(res));
