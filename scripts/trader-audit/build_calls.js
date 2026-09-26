// Builds the "call" dataset: FOMO first-buys (with the trader's own round trip) and Pump callouts.
const fs = require('fs');
const D = __dirname + '/data/';
const trades = JSON.parse(fs.readFileSync(D + 'fomo_trades.json'));
const callouts = JSON.parse(fs.readFileSync(D + 'pump_callouts.json'));

const ts = s => Date.parse(s.replace(' ', 'T') + (s.endsWith('Z') ? '' : 'Z'));
const CLOSED_FRAC = 0.05; // position counts as closed once under 5% of its peak size

// ---------- FOMO ----------
const pos = new Map(); // key h|n|a -> open position state
const calls = [];
for (const t of trades) {
  if (!t.p || t.p <= 0 || !t.a) continue;
  const key = `${t.h}|${t.n}|${t.a}`;
  const time = ts(t.c);
  const qty = (t.u || 0) / t.p;
  let s = pos.get(key);
  if (t.t === 'swap_buy') {
    if (!s || s.closed) {
      s = { call: { platform: 'fomo', h: t.h, a: t.a, n: t.n, k: t.k, t: time, p: t.p, m: t.m, u: t.u, eq: t.e, dev: !!t.dev,
                    adds: 0, addUsd: 0, soldUsd: 0, firstSellT: null, sold10mFrac: 0, closeT: null, costUsd: t.u },
            qty, peak: qty, closed: false, bought: qty };
      pos.set(key, s); calls.push(s.call);
    } else {
      s.qty += qty; s.bought += qty; s.peak = Math.max(s.peak, s.qty);
      s.call.adds++; s.call.addUsd += t.u || 0; s.call.costUsd += t.u || 0;
    }
  } else { // swap_sell / transfer_out
    if (!s || s.closed) continue; // position opened before capture window
    const q = Math.min(qty, s.qty);
    if (t.t === 'swap_sell') {
      s.call.soldUsd += q * t.p;
      if (s.call.firstSellT == null) s.call.firstSellT = time;
      if (time - s.call.t <= 10 * 60e3) s.call.sold10mFrac += q / s.bought;
    } else s.call.transferred = true;
    s.qty -= q;
    if (s.qty <= s.peak * CLOSED_FRAC) { s.closed = true; s.call.closeT = time; s.call.exitP = t.p; }
  }
}
for (const c of calls) {
  // realized ROI on the round trip (only meaningful when closed via sells)
  c.closed = c.closeT != null;
  c.roi = c.closed && !c.transferred && c.costUsd > 0 ? c.soldUsd / c.costUsd - 1 : null;
  c.holdMin = c.closed ? (c.closeT - c.t) / 60e3 : null;
  c.sold10mFrac = Math.min(1, c.sold10mFrac);
}

// ---------- Pump ----------
const pcalls = callouts.filter(c => c.a && c.p > 0).map(c => ({
  platform: 'pump', h: c.h, a: c.a, n: c.n, k: c.k, t: ts(c.c), p: +c.p, m: +c.m, id: c.id, wallet: c.w, x: c.x, quote: !!c.q,
}));

const all = calls.concat(pcalls);
fs.writeFileSync(D + 'calls.json', JSON.stringify(all));
const by = (arr, f) => arr.reduce((m, x) => (m[f(x)] = (m[f(x)] || 0) + 1, m), {});
console.log('fomo calls', calls.length, 'closed', calls.filter(c => c.closed).length);
console.log('fomo by chain', by(calls, c => c.n));
console.log('pump calls', pcalls.length, by(pcalls, c => c.n));
const since = require('./window').SINCE;
console.log('fomo calls last 30d', calls.filter(c => c.t >= since).length, 'distinct tokens', new Set(calls.filter(c => c.t >= since).map(c => c.n + c.a)).size);
console.log('pump distinct tokens', new Set(pcalls.map(c => c.n + c.a)).size);
