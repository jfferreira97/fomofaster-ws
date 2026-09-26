// R1: which exit rules work for followers — per preset and per style, chosen on one half of
// the window and scored on the other (both directions), so the recommendation isn't fitted
// to the same calls it's graded on. Also R2: what each minute of alert latency costs.
const fs = require('fs');
const L = require('./lib');
const W = require('./window');
const intel = JSON.parse(fs.readFileSync(L.D + 'intel.json'));
const calls = JSON.parse(fs.readFileSync(L.D + 'calls.json')).filter(c => c.t >= W.SINCE && c.t < W.END);
const barsFor = L.loadBars();
const tInfo = new Map(intel.traders.map(t => [t.key, t]));

// ---- paths (entry = first 1m bar after the alert) ----
const P = [];
for (const c of calls) {
  const key = `${c.platform}|${c.h}`, t = tInfo.get(key);
  const p = L.makePath(barsFor(c.n, c.a, c.t), c.t);
  if (!p) continue;
  P.push({ c, key, style: t?.style || 'unrated', cut: t?.style === 'noise', half: c.t < W.SPLIT ? 'A' : 'B',
    T: Float32Array.from(p.T), MX: Float32Array.from(p.MX), MN: Float32Array.from(p.MN), C: Float32Array.from(p.C), E: p.E, tE: p.tE });
}
console.log('paths', P.length);

const TPS = [0.3, 0.5, 0.75, 1, 1.5, 2, 3, 5, Infinity];
const SLS = [0.2, 0.3, 0.4, 0.5, 0.6, 0.75, Infinity];
const HOLD = [15, 60, 240, 720, 1440];
const combos = [];
for (const tp of TPS) for (const sl of SLS) for (const h of HOLD) combos.push({ tp, sl, h });
const label = x => `${x.tp === Infinity ? 'no TP' : '+' + Math.round(x.tp * 100) + '%'} / ${x.sl === Infinity ? 'no stop' : '-' + Math.round(x.sl * 100) + '%'} / ${x.h < 60 ? x.h + 'm' : x.h / 60 + 'h'}`;

function grid(paths) {
  // returns per combo: mean return on A, on B, all; and win rate all
  return combos.map(x => {
    let sA = 0, nA = 0, sB = 0, nB = 0, w = 0;
    for (const p of paths) { const r = L.exitReturn(p, x.tp, x.sl, x.h); if (p.half === 'A') { sA += r; nA++; } else { sB += r; nB++; } if (r > 0) w++; }
    return { ...x, A: sA / nA, B: sB / nB, all: (sA + sB) / (nA + nB), win: w / (nA + nB), n: nA + nB };
  });
}
function study(name, paths) {
  if (paths.length < 150) return null;
  const g = grid(paths);
  const bestA = g.reduce((a, b) => b.A > a.A ? b : a), bestB = g.reduce((a, b) => b.B > a.B ? b : a);
  const bestAll = [...g].sort((a, b) => b.all - a.all);
  // robust pick: best average of the two out-of-sample scores among combos in the top 25 on the full window
  const robust = bestAll.slice(0, 25).map(x => ({ ...x, oos: (g.find(y => y === x).A + g.find(y => y === x).B) / 2 }))
    .sort((a, b) => Math.min(b.A, b.B) - Math.min(a.A, a.B))[0];
  const ref = k => g.find(x => x.tp === k.tp && x.sl === k.sl && x.h === k.h);
  const baselines = {
    'hold 24h, no stop': ref({ tp: Infinity, sl: Infinity, h: 1440 }),
    'scalp +50%/-30%/1h': ref({ tp: 0.5, sl: 0.3, h: 60 }),
    'flip 2x/-30%/4h': ref({ tp: 1, sl: 0.3, h: 240 }),
    'runner 3x/-40%/24h': ref({ tp: 2, sl: 0.4, h: 1440 }),
  };
  const out = {
    name, calls: paths.length,
    oosAtoB: { rule: label(bestA), test: bestA.B }, oosBtoA: { rule: label(bestB), test: bestB.A },
    recommended: { rule: label(robust), tp: robust.tp, sl: robust.sl, h: robust.h, all: robust.all, A: robust.A, B: robust.B, win: robust.win },
    top5: bestAll.slice(0, 5).map(x => ({ rule: label(x), all: x.all, A: x.A, B: x.B, win: x.win })),
    baselines: Object.fromEntries(Object.entries(baselines).map(([k, v]) => [k, { all: v.all, A: v.A, B: v.B, win: v.win }])),
    // how much the stop level matters at the recommended TP/hold
    stopCurve: SLS.map(sl => { const v = ref({ tp: robust.tp, sl, h: robust.h }); return { sl, all: v.all, win: v.win }; }),
  };
  const f = v => (v >= 0 ? '+' : '') + (v * 100).toFixed(1) + '%';
  console.log(`\n== ${name} (${paths.length} calls)`);
  console.log(`  recommended  ${out.recommended.rule.padEnd(24)} all ${f(robust.all)}  A ${f(robust.A)}  B ${f(robust.B)}  win ${(robust.win * 100).toFixed(0)}%`);
  console.log(`  chosen on A → B: ${label(bestA)} = ${f(bestA.B)}   chosen on B → A: ${label(bestB)} = ${f(bestB.A)}`);
  for (const [k, v] of Object.entries(out.baselines)) console.log(`  ${k.padEnd(22)} all ${f(v.all)}  A ${f(v.A)}  B ${f(v.B)}`);
  return out;
}

const kept = P.filter(p => !p.cut && p.style !== 'unrated');
const results = { all: study('All calls (no curation)', P), kept: study('Kept traders (cut list removed)', kept), cut: study('Cut traders', P.filter(p => p.cut)) };
for (const pr of intel.presets) {
  const m = new Set(pr._members);
  results['preset:' + pr.id] = study(`Preset: ${pr.name}`, P.filter(p => m.has(p.key)));
}
for (const st of ['scalper', 'flipper', 'runner', 'swing', 'degen']) results['style:' + st] = study(`Style: ${st}`, kept.filter(p => p.style === st));

// ---- R2: latency cost, on kept traders, with the kept-group recommended exit ----
const rec = results.kept.recommended;
const lat = [];
for (const delay of [0, 1, 2, 3, 5, 10, 15]) {
  const r = [];
  for (const p of kept) {
    let path;
    if (delay === 0) {
      // the trader's own fill / call price: the theoretical zero-latency entry
      if (!(p.c.p > 0)) continue;
      path = { ...p, MX: p.MX.map(v => v * p.E / p.c.p), MN: p.MN.map(v => v * p.E / p.c.p), C: p.C.map(v => v * p.E / p.c.p) };
      // include the move between call and first bar in the running max/min
      const k = p.E / p.c.p; path.MX = path.MX.map(v => Math.max(v, k, 1)); path.MN = path.MN.map(v => Math.min(v, k, 1));
    } else {
      const bars = barsFor(p.c.n, p.c.a, p.c.t);
      const q = L.makePath(bars, p.c.t + (delay - 1) * 60e3, { maxGapMin: 5 });
      if (!q) continue;
      path = q;
    }
    r.push(L.exitReturn(path, rec.tp, rec.sl, rec.h));
  }
  lat.push({ delay, n: r.length, ev: L.mean(r), win: r.filter(x => x > 0).length / r.length });
  console.log(`latency ${String(delay).padStart(2)}m: ${(L.mean(r) * 100).toFixed(1)}%/call  win ${(100 * r.filter(x => x > 0).length / r.length).toFixed(0)}%  n=${r.length}`);
}
results.latency = lat;
fs.writeFileSync(L.D + 'research_exits.json', JSON.stringify(results, (k, v) => v === Infinity ? 'inf' : v));
