// Follower-outcome analysis: for every call, simulate what a user who acted on the alert got,
// then roll up per trader. Output: data/call_metrics.json, data/trader_metrics.json
const fs = require('fs');
const D = __dirname + '/data/';
const W = require('./window');
const SINCE = W.SINCE;
const NOW = W.END;
const MIN = 60e3, H = 60 * MIN;
const COST = 0.03; // round-trip fees + slippage on small caps

// ---------- load candles ----------
const files = new Map(); // k -> [{from,to,bars}]
for (const f of fs.readdirSync(D + 'candles')) {
  const j = JSON.parse(fs.readFileSync(D + 'candles/' + f));
  if (!j.bars.length) continue;
  j.bars = require('./lib').cleanBars(j.bars);
  (files.get(j.k) || files.set(j.k, []).get(j.k)).push(j);
}
// Fallback where no candle source served the token (mostly Robinhood/BNB/Base): a price path
// built from every trade we captured in that token, from any trader. Sparse — highs and lows
// between prints are missed — so it needs 5+ prints in the 24h after the call to be used.
const tape = new Map();
{
  const tsOf = s => Date.parse(s.endsWith('Z') ? s : s.replace(' ', 'T') + 'Z');
  for (const t of JSON.parse(fs.readFileSync(D + 'fomo_trades.json'))) {
    if (!(t.p > 0) || t.t === 'transfer_out') continue;
    const k = `${t.n}_${t.a}`; (tape.get(k) || tape.set(k, []).get(k)).push([tsOf(t.c), t.p]);
  }
  for (const c of JSON.parse(fs.readFileSync(D + 'pump_callouts.json'))) {
    if (!(+c.p > 0)) continue;
    const k = `${c.n}_${c.a}`; (tape.get(k) || tape.set(k, []).get(k)).push([tsOf(c.c), +c.p]);
  }
  for (const v of tape.values()) v.sort((a, b) => a[0] - b[0]);
}
function tapeBars(c) {
  const pts = (tape.get(`${c.n}_${c.a}`) || []).filter(([t]) => t > c.t && t <= c.t + 24 * H);
  if (pts.length < 5) return null;
  return pts.map(([t, p]) => [t, p, p, p, p, 0]);
}
let tapeUsed = 0;
const barsFor = c => {
  const b = (files.get(`${c.n}_${c.a}`) || []).find(g => c.t >= g.from && c.t <= g.to)?.bars;
  if (b) return b;
  const tb = tapeBars(c);
  if (tb) tapeUsed++;
  return tb;
};

// ---------- per-call simulation ----------
// bars: [openMs, o, h, l, c, vol], sparse (only minutes with trades)
function simulate(c, bars) {
  // follower enters on the first bar that opens after the alert (0-60s latency), at its open
  const i0 = bars.findIndex(b => b[0] >= c.t);
  if (i0 < 0 || bars[i0][0] - c.t > 5 * MIN) return null; // no trading near the call → can't evaluate
  const tE = bars[i0][0], E = bars[i0][1];
  if (!(E > 0)) return null;
  const path = [];
  for (let i = i0; i < bars.length && bars[i][0] <= tE + 24 * H; i++) path.push(bars[i]);
  const closeAt = t => { let p = E; for (const b of path) { if (b[0] > t) break; p = b[4]; } return p / E - 1; };
  const peakWithin = t => { let m = E; for (const b of path) { if (b[0] > tE + t) break; m = Math.max(m, b[2]); } return m / E; };
  let peak = E, tPeak = tE, low = E, ddBeforePeak = 0;
  for (const b of path) {
    if (b[2] > peak) { peak = b[2]; tPeak = b[0]; ddBeforePeak = Math.min(ddBeforePeak, low / E - 1); }
    low = Math.min(low, b[3]);
  }
  // take-profit / stop-loss / time-stop; when one bar spans both, assume the stop hit first
  const strat = (tp, sl, maxT) => {
    for (const b of path) {
      if (b[0] > tE + maxT) break;
      const lo = b[3] / E - 1, hi = b[2] / E - 1;
      if (lo <= -sl) return -sl - COST;
      if (hi >= tp) return tp - COST;
    }
    return closeAt(tE + maxT) - COST;
  };
  // did it reach +X% before -Y%? (first passage, 24h)
  const firstPassage = (up, dn) => {
    for (const b of path) { if (b[3] / E - 1 <= -dn) return false; if (b[2] / E - 1 >= up) return true; }
    return false;
  };
  const entryVsCall = c.p > 0 ? E / c.p - 1 : null;
  return {
    entryVsCall,
    r5m: closeAt(tE + 5 * MIN), r15m: closeAt(tE + 15 * MIN), r1h: closeAt(tE + H), r4h: closeAt(tE + 4 * H), r24h: closeAt(tE + 24 * H),
    pk5m: peakWithin(5 * MIN), pk15m: peakWithin(15 * MIN), pk1h: peakWithin(H), pk4h: peakWithin(4 * H), pk24h: peak / E,
    tPeakMin: (tPeak - tE) / MIN, ddBeforePeak, minLow24h: low / E - 1,
    // follower strategies
    scalp: strat(0.5, 0.3, H),          // +50% / -30%, out by 1h
    flip: strat(1.0, 0.3, 4 * H),       // 2x / -30%, out by 4h
    runner: strat(2.0, 0.4, 24 * H),    // 3x / -40%, out by 24h
    hold1h: closeAt(tE + H) - COST,
    fp50_30: firstPassage(0.5, 0.3), fp100_30: firstPassage(1.0, 0.3), fp100_40: firstPassage(1.0, 0.4), fp200_40: firstPassage(2.0, 0.4),
    sl30: path.some(b => b[0] <= tE + 4 * H && b[3] / E - 1 <= -0.3), sl40: path.some(b => b[0] <= tE + 4 * H && b[3] / E - 1 <= -0.4),
  };
}

const calls = JSON.parse(fs.readFileSync(D + 'calls.json')).filter(c => c.t >= SINCE);
const out = [];
let noData = 0;
for (const c of calls) {
  const bars = barsFor(c);
  const s = bars ? simulate(c, bars) : null;
  if (!s) noData++;
  out.push({ platform: c.platform, h: c.h, a: c.a, n: c.n, k: c.k, t: c.t, m: c.m, u: c.u, dev: c.dev, quote: c.quote,
    roi: c.roi, holdMin: c.holdMin, sold10mFrac: c.sold10mFrac, closed: c.closed, sim: s });
}
fs.writeFileSync(D + 'call_metrics.json', JSON.stringify(out));
console.log(`calls ${calls.length}, simulated ${calls.length - noData}, no data ${noData}, tape-path ${tapeUsed}`);
{
  const NET = { 1399811149: 'SOL', 4663: 'ROBINHOOD', 56: 'BNB', 8453: 'BASE', 1: 'ETH', 5042: 'ARC', 143: 'MONAD' };
  const cov = {};
  for (const c of out) { const k = `${NET[c.n] || 'other'}/${c.platform}`; cov[k] ||= { k, n: 0, ok: 0 }; cov[k].n++; if (c.sim) cov[k].ok++; }
  fs.writeFileSync(D + 'coverage.json', JSON.stringify(Object.values(cov).sort((a, b) => b.n - a.n)));
}

// ---------- alert volume (what users actually receive) ----------
const days = (NOW - SINCE) / (24 * H);
const alerts = {};
const bump = (key, kind) => { alerts[key] = alerts[key] || { buy: 0, sell: 0, callout: 0, thesis: 0 }; alerts[key][kind]++; };
const tsOf = s => Date.parse(s.endsWith('Z') ? s : s.replace(' ', 'T') + 'Z');
for (const t of JSON.parse(fs.readFileSync(D + 'fomo_trades.json'))) if (tsOf(t.c) >= SINCE && t.t !== 'transfer_out') bump('fomo|' + t.h, t.t === 'swap_buy' ? 'buy' : 'sell');
for (const t of JSON.parse(fs.readFileSync(D + 'fomo_thesis.json'))) if (t.c && tsOf(t.c) >= SINCE) bump('fomo|' + t.h, 'thesis');
for (const c of JSON.parse(fs.readFileSync(D + 'pump_callouts.json'))) if (tsOf(c.c) >= SINCE) bump('pump|' + c.h, 'callout');

// ---------- per-trader rollup ----------
const q = (arr, p) => { if (!arr.length) return null; const s = [...arr].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(p * s.length))]; };
const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null;
const rate = (arr, f) => arr.length ? arr.filter(f).length / arr.length : null;
const groups = new Map();
for (const c of out) { const k = `${c.platform}|${c.h}`; (groups.get(k) || groups.set(k, []).get(k)).push(c); }
const traders = [];
for (const [key, cs] of groups) {
  const [platform, h] = key.split('|');
  const sims = cs.filter(c => c.sim).map(c => c.sim);
  const mc = cs.map(c => c.m).filter(x => x > 0);
  const own = cs.filter(c => c.roi != null);
  const al = alerts[key] || { buy: 0, sell: 0, callout: 0, thesis: 0 };
  const alertCount = platform === 'fomo' ? al.buy + al.sell : al.callout;
  // "dump on followers": trader dumped most of the bag within 10m, and the follower lost
  const dumps = cs.filter(c => c.sim && c.sold10mFrac >= 0.5);
  traders.push({
    platform, h, calls: cs.length, callsPerDay: cs.length / days, simulated: sims.length,
    alertsPerDay: alertCount / days, alerts: al,
    mcapMedian: q(mc, 0.5), mcapP25: q(mc, 0.25), mcapMean: mean(mc), microShare: rate(mc, m => m < 50e3),
    // follower outcomes
    pk15mMed: q(sims.map(s => s.pk15m), 0.5), pk24hMed: q(sims.map(s => s.pk24h), 0.5),
    hit50in15m: rate(sims, s => s.pk15m >= 1.5), hit2xIn1h: rate(sims, s => s.pk1h >= 2), hit2xIn24h: rate(sims, s => s.pk24h >= 2), hit3xIn24h: rate(sims, s => s.pk24h >= 3),
    fp50_30: rate(sims, s => s.fp50_30), fp100_30: rate(sims, s => s.fp100_30), fp100_40: rate(sims, s => s.fp100_40), fp200_40: rate(sims, s => s.fp200_40),
    sl30: rate(sims, s => s.sl30), sl40: rate(sims, s => s.sl40),
    tPeakMed: q(sims.filter(s => s.pk24h >= 1.3).map(s => s.tPeakMin), 0.5),
    r1hMed: q(sims.map(s => s.r1h), 0.5), r24hMed: q(sims.map(s => s.r24h), 0.5),
    evScalp: mean(sims.map(s => s.scalp)), evFlip: mean(sims.map(s => s.flip)), evRunner: mean(sims.map(s => s.runner)), evHold1h: mean(sims.map(s => Math.max(-1, s.hold1h))),
    winScalp: rate(sims, s => s.scalp > 0), winFlip: rate(sims, s => s.flip > 0), winRunner: rate(sims, s => s.runner > 0),
    entryVsCallMed: q(sims.map(s => s.entryVsCall).filter(x => x != null), 0.5),
    // trader's own behaviour (FOMO only — reconstructed from their buys/sells)
    ownWin: rate(own, c => c.roi > 0), ownRoiMed: q(own.map(c => c.roi), 0.5), ownHoldMed: q(cs.filter(c => c.holdMin != null).map(c => c.holdMin), 0.5),
    ownRealizedUsd: own.length ? cs.reduce((s, c) => s + (c.roi != null && c.u ? c.roi * c.u : 0), 0) : null,
    fastDumpShare: platform === 'fomo' ? rate(cs, c => c.sold10mFrac >= 0.5) : null,
    dumpFollowerLoss: dumps.length ? rate(dumps, c => c.sim.scalp < 0) : null,
    devShare: rate(cs, c => c.dev),
  });
}
fs.writeFileSync(D + 'trader_metrics.json', JSON.stringify(traders));
console.log('traders', traders.length, 'with >=10 simulated', traders.filter(t => t.simulated >= 10).length);
