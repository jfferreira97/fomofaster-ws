// Shared price-path machinery for the research scripts: loads the cached candles (plus the
// trade-tape fallback) and turns a call into a compact path that answers "when did price
// first reach X" in O(log n) via running max/min arrays.
const fs = require('fs');
const D = __dirname + '/data/';
const MIN = 60e3, H = 60 * MIN;
const tsOf = s => Date.parse(s.endsWith('Z') ? s : s.replace(' ', 'T') + 'Z');

// The candle service occasionally serves impossible prints on EVM tokens: a single 1m high of
// 1e28x, or the series switching price units mid-way (sustained 1e6x jumps). Against a rolling
// median of the last 5 closes: clamp wicks to 6x/÷6, and end the series at the first bar whose
// close is 30x away (real memecoins do run 20-30x in a day, but not within one minute).
function cleanBars(bars) {
  const out = [], last = [];
  const ref = () => { const s = [...last].sort((a, b) => a - b); return s[s.length >> 1]; };
  for (const [t, o, h, l, c, v] of bars) {
    if (!(o > 0 && c > 0)) continue;
    if (last.length) {
      const r = ref();
      if (c > r * 30 || c < r / 30 || o > r * 30 || o < r / 30) break;
      out.push([t, o, Math.min(h, Math.max(o, c, r) * 6), Math.max(l, Math.min(o, c, r) / 6), c, v]);
    } else out.push([t, o, Math.min(h, Math.max(o, c) * 6), Math.max(l, Math.min(o, c) / 6), c, v]);
    last.push(c); if (last.length > 5) last.shift();
  }
  return out;
}

function loadBars() {
  const files = new Map();
  for (const f of fs.readdirSync(D + 'candles')) {
    const j = JSON.parse(fs.readFileSync(D + 'candles/' + f));
    if (!j.bars.length) continue;
    j.bars = cleanBars(j.bars);
    (files.get(j.k) || files.set(j.k, []).get(j.k)).push(j);
  }
  const tape = new Map();
  const push = (k, t, p) => (tape.get(k) || tape.set(k, []).get(k)).push([t, p]);
  for (const t of JSON.parse(fs.readFileSync(D + 'fomo_trades.json'))) if (t.p > 0 && t.t !== 'transfer_out') push(`${t.n}_${t.a}`, tsOf(t.c), t.p);
  for (const c of JSON.parse(fs.readFileSync(D + 'pump_callouts.json'))) if (+c.p > 0) push(`${c.n}_${c.a}`, tsOf(c.c), +c.p);
  for (const v of tape.values()) v.sort((a, b) => a[0] - b[0]);
  // bars covering time t for token (n,a): candle group if one spans t, else tape points
  return (n, a, t) => {
    const g = (files.get(`${n}_${a}`) || []).find(g => t >= g.from - 5 * MIN && t <= g.to + 24 * H);
    if (g) return g.bars;
    const pts = (tape.get(`${n}_${a}`) || []).filter(([x]) => x > t && x <= t + 30 * H);
    return pts.length >= 5 ? pts.map(([x, p]) => [x, p, p, p, p, 0]) : null;
  };
}

// Path from an entry at time t (first bar opening at/after t, within maxGap), up to horizon.
// Returns relative arrays: time offset (min), running max of highs, running min of lows, close.
function makePath(bars, t, { maxGapMin = 5, horizonH = 24, entryPrice = null } = {}) {
  if (!bars) return null;
  let i0 = -1;
  for (let i = 0; i < bars.length; i++) if (bars[i][0] >= t) { i0 = i; break; }
  if (i0 < 0 || bars[i0][0] - t > maxGapMin * MIN) return null;
  const tE = bars[i0][0], E = entryPrice ?? bars[i0][1];
  if (!(E > 0)) return null;
  const T = [], MX = [], MN = [], C = [];
  let mx = -Infinity, mn = Infinity;
  for (let i = i0; i < bars.length && bars[i][0] <= tE + horizonH * H; i++) {
    const b = bars[i];
    mx = Math.max(mx, b[2] / E); mn = Math.min(mn, b[3] / E);
    T.push((b[0] - tE) / MIN); MX.push(mx); MN.push(mn); C.push(b[4] / E);
  }
  return { tE, E, T, MX, MN, C };
}
// first index where arr (monotone) crosses; asc=true for running max >= x, false for running min <= x
function firstIdx(arr, x, asc) {
  let lo = 0, hi = arr.length;
  while (lo < hi) { const m = (lo + hi) >> 1; if (asc ? arr[m] >= x : arr[m] <= x) hi = m; else lo = m + 1; }
  return lo; // arr.length = never
}
function lastIdxBefore(T, minutes) { // last bar with T <= minutes
  let lo = 0, hi = T.length; while (lo < hi) { const m = (lo + hi) >> 1; if (T[m] <= minutes) lo = m + 1; else hi = m; } return lo - 1;
}
const COST = 0.03;
// take-profit / stop / time-stop; tp or sl = Infinity to disable. Ties go to the stop.
function exitReturn(p, tp, sl, maxMin) {
  const end = lastIdxBefore(p.T, maxMin);
  const iTp = tp === Infinity ? Infinity : firstIdx(p.MX, 1 + tp, true);
  const iSl = sl === Infinity ? Infinity : firstIdx(p.MN, 1 - sl, false);
  if (iSl <= end && iSl <= iTp) return -sl - COST;
  if (iTp <= end) return tp - COST;
  return (end >= 0 ? p.C[end] : 1) - 1 - COST;
}
// value at an arbitrary exit time (e.g. when the trader sells), with optional protective stop
function exitAtTime(p, minutes, sl = Infinity) {
  const end = lastIdxBefore(p.T, minutes);
  const iSl = sl === Infinity ? Infinity : firstIdx(p.MN, 1 - sl, false);
  if (iSl <= end) return -sl - COST;
  return (end >= 0 ? p.C[end] : 1) - 1 - COST;
}
const mean = a => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null;
const med = a => { if (!a.length) return null; const s = [...a].sort((x, y) => x - y); return s[Math.floor(s.length / 2)]; };
module.exports = { cleanBars, D, MIN, H, tsOf, loadBars, makePath, exitReturn, exitAtTime, firstIdx, lastIdxBefore, mean, med, COST };
