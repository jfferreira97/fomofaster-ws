// Which trader traits are stable over time, and which half-A traits predict half-B follower EV?
// Spearman rank correlations, traders with 8+ simulated calls in both halves.
const fs = require('fs');
const D = __dirname + '/data/';
const calls = JSON.parse(fs.readFileSync(D + 'call_metrics.json')).filter(c => c.sim);
const SPLIT = require('./window').SPLIT;
const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null;
const med = a => { if (!a.length) return null; const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
const rate = (a, f) => a.length ? a.filter(f).length / a.length : null;

function traits(cs) {
  const s = cs.map(c => c.sim);
  return {
    n: cs.length,
    evScalp: mean(s.map(x => x.scalp)), evFlip: mean(s.map(x => x.flip)), evRunner: mean(s.map(x => x.runner)),
    evBestFixed: null,
    medScalp: med(s.map(x => x.scalp)),
    hit50in15m: rate(s, x => x.pk15m >= 1.5), hit2x24: rate(s, x => x.pk24h >= 2), hit3x24: rate(s, x => x.pk24h >= 3),
    fp50_30: rate(s, x => x.fp50_30), fp100_30: rate(s, x => x.fp100_30),
    sl30: rate(s, x => x.sl30), sl40: rate(s, x => x.sl40),
    r1hMed: med(s.map(x => x.r1h)), r24hMed: med(s.map(x => x.r24h)),
    tPeak: med(s.filter(x => x.pk24h >= 1.3).map(x => x.tPeakMin)),
    late: med(s.map(x => x.entryVsCall).filter(x => x != null)),
    lateShare: rate(s.filter(x => x.entryVsCall != null), x => x.entryVsCall > 0.1),
    mcap: med(cs.map(c => c.m).filter(x => x > 0)),
    logMcap: Math.log10(med(cs.map(c => c.m).filter(x => x > 0)) || 1),
    perDay: cs.length / 15,
    ownWin: rate(cs.filter(c => c.roi != null), c => c.roi > 0),
    buySize: med(cs.map(c => c.u).filter(x => x > 0)),
  };
}
const split = { A: {}, B: {} };
for (const c of calls) (split[c.t < SPLIT ? 'A' : 'B'][`${c.platform}|${c.h}`] ||= []).push(c);
const keys = Object.keys(split.A).filter(k => split.A[k].length >= 8 && (split.B[k] || []).length >= 8);
const TA = Object.fromEntries(keys.map(k => [k, traits(split.A[k])]));
const TB = Object.fromEntries(keys.map(k => [k, traits(split.B[k])]));

const rank = a => { const idx = a.map((v, i) => [v, i]).sort((x, y) => x[0] - y[0]); const r = Array(a.length); idx.forEach(([, i], j) => r[i] = j); return r; };
const spearman = (x, y) => { const ok = x.map((v, i) => v != null && y[i] != null && !isNaN(v) && !isNaN(y[i])).map((b, i) => b ? i : -1).filter(i => i >= 0);
  const rx = rank(ok.map(i => x[i])), ry = rank(ok.map(i => y[i])); const mx = mean(rx), my = mean(ry);
  let sxy = 0, sx = 0, sy = 0; for (let i = 0; i < rx.length; i++) { sxy += (rx[i] - mx) * (ry[i] - my); sx += (rx[i] - mx) ** 2; sy += (ry[i] - my) ** 2; }
  return { rho: sxy / Math.sqrt(sx * sy), n: rx.length }; };

const names = Object.keys(TA[keys[0]]).filter(k => k !== 'evBestFixed' && k !== 'n');
console.log(`traders with 8+ calls in both halves: ${keys.length}\n`);
console.log('trait'.padEnd(12), 'stable A→B', ' predicts B evScalp', ' B evFlip', ' B evRunner');
const out = {};
for (const t of names) {
  const a = keys.map(k => TA[k][t]);
  const stab = spearman(a, keys.map(k => TB[k][t]));
  const ps = spearman(a, keys.map(k => TB[k].evScalp)), pf = spearman(a, keys.map(k => TB[k].evFlip)), pr = spearman(a, keys.map(k => TB[k].evRunner));
  out[t] = { stable: stab.rho, scalp: ps.rho, flip: pf.rho, runner: pr.rho, n: stab.n };
  console.log(t.padEnd(12), stab.rho.toFixed(2).padStart(10), ps.rho.toFixed(2).padStart(19), pf.rho.toFixed(2).padStart(9), pr.rho.toFixed(2).padStart(11), ` n=${stab.n}`);
}
fs.writeFileSync(D + 'persistence.json', JSON.stringify({ n: keys.length, traits: out }));
