// Turns trader_metrics + profiles into: style per trader, exclude list with reasons, presets.
// Output: data/intel.json (report input) and trader-intel.json (the app's Intel file).
const fs = require('fs');
const D = __dirname + '/data/';
const tm = JSON.parse(fs.readFileSync(D + 'trader_metrics.json'));
const prof = JSON.parse(fs.readFileSync(D + 'profiles.json'));
const profBy = new Map(prof.map(p => [`${p.platform}|${p.h}`, p]));
const tradersDb = JSON.parse(fs.readFileSync(D + 'traders.json')); // everyone we track
const MIN_N = 8;            // simulated calls needed to rate at all
const K = 12;               // shrinkage strength (pseudo-calls at the population mean)

const mean = a => a.reduce((x, y) => x + y, 0) / a.length;
const rated = tm.filter(t => t.simulated >= MIN_N);
const pop = {
  scalp: mean(rated.map(t => t.evScalp)), flip: mean(rated.map(t => t.evFlip)), runner: mean(rated.map(t => t.evRunner)),
  hit2x: mean(rated.map(t => t.hit2xIn24h)), sl30: mean(rated.map(t => t.sl30)),
};
// Small samples get pulled toward the population average so one lucky 10x doesn't top the board
const shrink = (v, n, prior) => (v * n + prior * K) / (n + K);

for (const t of tm) {
  const n = t.simulated;
  t.key = `${t.platform}|${t.h}`;
  t.profile = profBy.get(t.key) || null;
  if (n < MIN_N) { t.style = 'unrated'; continue; }
  t.sScalp = shrink(t.evScalp, n, pop.scalp);
  t.sFlip = shrink(t.evFlip, n, pop.flip);
  t.sRunner = shrink(t.evRunner, n, pop.runner);
  t.sHit2x = shrink(t.hit2xIn24h, n, pop.hit2x);
  const best = Math.max(t.sScalp, t.sFlip, t.sRunner);
  t.bestEv = best;
  t.bestStrat = best === t.sScalp ? 'scalp' : best === t.sFlip ? 'flip' : 'runner';
  const m = t.mcapMedian ?? 0;

  // --- noise / exclusion reasons (follower's view, not the trader's) ---
  const reasons = [];
  if (best < -0.05) reasons.push(`followers lose ${(-best * 100).toFixed(0)}% per call even with the best exit style`);
  else if (best < -0.02) reasons.push(`negative expectancy for followers (${(best * 100).toFixed(1)}% per call, best exit style)`);
  if (t.alertsPerDay >= 20 && best < 0.02) reasons.push(`${t.alertsPerDay.toFixed(0)} alerts/day with no edge`);
  if (t.sl30 >= 0.6 && t.hit2xIn24h < 0.15) reasons.push(`${(t.sl30 * 100).toFixed(0)}% of calls hit a -30% stop within 4h`);
  if (t.fastDumpShare >= 0.35 && t.dumpFollowerLoss >= 0.6) reasons.push(`dumps ${(t.fastDumpShare * 100).toFixed(0)}% of entries within 10 min; followers lost on ${(t.dumpFollowerLoss * 100).toFixed(0)}% of those`);
  if (t.entryVsCallMed > 0.15) reasons.push(`price already +${(t.entryVsCallMed * 100).toFixed(0)}% by the time followers can enter`);
  t.reasons = reasons;
  const noisy = best < -0.02 || (t.alertsPerDay >= 20 && best < 0.02) || (t.fastDumpShare >= 0.35 && t.dumpFollowerLoss >= 0.6 && best < 0.02);

  // --- style ---
  // fast movers first: a 3x that lasts 3 minutes is still a win if the alert gets you in
  const fast = t.tPeakMed != null && t.tPeakMed <= 20 && (t.bestStrat === 'scalp' || t.hit50in15m >= 0.35);
  if (noisy) t.style = 'noise';
  else if (m >= 5e6) t.style = 'swing';
  else if (fast) t.style = 'scalper';
  else if (m < 30e3 && best < 0.03) t.style = 'degen';
  else if (t.bestStrat === 'runner') t.style = 'runner';
  else t.style = 'flipper';
}

// ---------- composite score (validated out of sample, see select_score.js) ----------
// z(2x rate) + z(median 1h return) - z(-30% stop-out rate) + z(EV averaged over all three exit
// styles). Everything heavily shrunk: past EV alone does not persist (rank corr ~0.2, the top
// mean-reverts), while hit rates and stop-out rates do.
{
  const R = tm.filter(t => t.style !== 'unrated');
  const sh = (v, n, p, k) => (v * n + p * k) / (n + k);
  for (const t of R) {
    t.evAll = (sh(t.evScalp, t.simulated, pop.scalp, 30) + sh(t.evFlip, t.simulated, pop.flip, 30) + sh(t.evRunner, t.simulated, pop.runner, 30)) / 3;
    t.h2Shr = sh(t.hit2xIn24h, t.simulated, pop.hit2x, 20);
    t.slShr = sh(t.sl30, t.simulated, pop.sl30, 20);
  }
  const z = f => { const v = R.map(f), m = mean(v), sd = Math.sqrt(mean(v.map(x => (x - m) ** 2))) || 1; return t => (f(t) - m) / sd; };
  const zh = z(t => t.h2Shr), zr = z(t => t.r1hMed ?? 0), zs = z(t => t.slShr), ze = z(t => t.evAll);
  for (const t of R) t.score = zh(t) + zr(t) - zs(t) + ze(t);
  const sorted = R.map(t => t.score).sort((a, b) => a - b);
  const p30 = sorted[Math.floor(sorted.length * 0.3)], p20 = sorted[Math.floor(sorted.length * 0.2)];
  // cut: bottom 30% by score that also lose money on average across exit styles, or high-volume no-edge
  for (const t of R) {
    const cut = (t.score <= p30 && t.evAll < 0) || t.evAll < -0.06 || (t.alertsPerDay >= 20 && t.evAll < 0.005);
    if (cut && t.style !== 'noise') { t.style = 'noise'; }
    // hard = strong evidence (remove from tracking); soft = weak, hide from presets/defaults
    t.cutTier = cut ? ((t.score <= p20 || t.evAll < -0.08 || (t.alertsPerDay >= 20 && t.evAll < 0)) ? 'hard' : 'soft') : null;
    if (!cut && t.style === 'noise') {
      // was flagged by the single-EV rule but the robust score disagrees: re-style it
      const m = t.mcapMedian ?? 0;
      t.style = m >= 5e6 ? 'swing' : (t.tPeakMed != null && t.tPeakMed <= 20) ? 'scalper' : m < 30e3 ? 'degen' : t.bestStrat === 'runner' ? 'runner' : 'flipper';
    }
    if (cut && t.score <= p30 && !t.reasons.length) t.reasons.push('bottom 30% on the combined score (2x rate, 1h return, stop-outs, EV)');
    if (cut && t.alertsPerDay >= 20 && t.evAll < 0.005 && !t.reasons.some(r => r.includes('alerts/day'))) t.reasons.push(`${t.alertsPerDay.toFixed(0)} alerts/day with no edge`);
  }
}

const fmtUsd = v => v == null ? '?' : v >= 1e6 ? `$${(v / 1e6).toFixed(1)}M` : v >= 1e3 ? `$${(v / 1e3).toFixed(0)}K` : `$${Math.round(v)}`;
const pc = v => v == null ? '?' : `${Math.round(v * 100)}%`;
function summary(t) {
  if (t.style === 'unrated') return null;
  const strat = { scalp: 'scalping (+50% / -30%, out by 1h)', flip: 'flipping (2x / -30%, out by 4h)', runner: 'holding for a runner (3x / -40%, 24h)' }[t.bestStrat];
  return `Best played by ${strat}. ` +
    `${pc(t.hit2xIn24h)} of calls hit 2x within 24h, ${pc(t.sl30)} tagged -30% within 4h. Median entry ${fmtUsd(t.mcapMedian)}.`;
}

// ---------- alert levels (mirrors AlertBudgetService.PickFor) ----------
// A user picks how many alerts/hour they want; the app follows the best-scored traders that
// fit. Simulated here for a user receiving every alert type, for the report.
const LEVELS = [
  { id: 'quiet', name: 'Quiet', alertsPerHour: 2, description: 'Only the very best, a couple of alerts an hour.' },
  { id: 'light', name: 'Light', alertsPerHour: 5, description: 'Top-rated traders, about one alert every 12 minutes.' },
  { id: 'balanced', name: 'Balanced', alertsPerHour: 10, description: 'A solid spread of proven traders across styles.' },
  { id: 'active', name: 'Active', alertsPerHour: 20, description: 'More coverage, including decent-but-not-elite traders.' },
  { id: 'heavy', name: 'Heavy', alertsPerHour: 40, description: 'Most traders with a positive record.' },
  { id: 'max', name: 'Everything but noise', alertsPerHour: 0, description: 'Every tracked trader except the cut list, including new ones still being rated.' },
];
const rankedForLevels = tm.filter(t => t.style !== 'noise').sort((a, b) => (b.score ?? -1e9) - (a.score ?? -1e9));
// Nested: each level keeps everything the level below picked and fills its extra budget, so
// moving up never drops a trader (plain budget-greedy would let one loud trader crowd out
// several quiet ones at the next level up).
function pickLevel(budget) {
  const steps = [...LEVELS.map(l => l.alertsPerHour).filter(x => x > 0 && (budget <= 0 || x < budget)), budget].sort((x, y) => (x || Infinity) - (y || Infinity));
  const out = [], inSet = new Set(); let used = 0;
  for (const step of steps) for (const t of rankedForLevels) {
    if (inSet.has(t.key)) continue;
    if (step > 0 && t.score == null) continue;
    const r = t.alertsPerDay / 24;
    if (step > 0 && used + r > step) continue;
    used += r; out.push(t); inSet.add(t.key);
  }
  return out;
}
const levels = LEVELS.map(l => { const m = pickLevel(l.alertsPerHour); return { ...l, _members: m.map(t => t.key), _perHour: m.reduce((s, t) => s + t.alertsPerDay / 24, 0),
  _avgEv: mean(m.filter(t => t.evAll != null).map(t => t.evAll)) }; });
// kept for the report's older sections
const presets = [];

// ---------- exclusions: rated noise + tracked-but-dead traders ----------
const tracked = new Set(tradersDb.map(t => `${t.pl.toLowerCase()}|${t.h}`));
const exclude = tm.filter(t => t.style === 'noise').sort((a, b) => (a.cutTier === b.cutTier ? 0 : a.cutTier === 'hard' ? -1 : 1) || a.score - b.score);
const weak = tm.filter(t => t.style !== 'noise' && t.style !== 'unrated' && t.evAll < -0.03);
const active = new Set(tm.map(t => t.key));
const dormant = tradersDb.filter(t => !active.has(`${t.pl.toLowerCase()}|${t.h}`));

// ---------- write ----------
const CH = { SOL: 'SOL', ROBINHOOD: 'ROBINHOOD', BNB: 'BNB', BASE: 'BASE', ETH: 'ETH', ARC: 'ARC', MONAD: 'MONAD' };
const capTier = m => m == null ? null : m < 50e3 ? 'micro' : m < 500e3 ? 'small' : m < 5e6 ? 'mid' : 'large';
const chainFocus = ch => { const e = Object.entries(ch || {}); const tot = e.reduce((s, [, v]) => s + v, 0); if (!tot) return null;
  const [k, v] = e.sort((x, y) => y[1] - x[1])[0]; return v / tot >= 0.6 ? (CH[k] || String(k)) : 'Multi'; };
const speed = m => m == null ? null : m <= 20 ? 'minutes' : m <= 240 ? 'hours' : 'day';
const r3 = v => v == null || isNaN(v) ? null : Math.round(v * 1000) / 1000;
// avatars from the raw feeds, and each trader's most recent replayed calls for the profile card
const avatar = new Map((fs.existsSync(D + 'avatars.json') ? JSON.parse(fs.readFileSync(D + 'avatars.json')) : []).map(a => [`${a.pl}|${a.h}`, a.img]));
const NETL = { 1399811149: 'SOL', 4663: 'ROBINHOOD', 56: 'BNB', 8453: 'BASE', 1: 'ETH', 5042: 'ARC', 143: 'MONAD' };
const recent = new Map();
for (const c of JSON.parse(fs.readFileSync(D + 'call_metrics.json'))) {
  if (!c.sim) continue;
  const k = `${c.platform}|${c.h}`; (recent.get(k) || recent.set(k, []).get(k)).push(c);
}
const recentCalls = key => (recent.get(key) || []).sort((a, b) => b.t - a.t).slice(0, 12).map(c => ({
  t: new Date(c.t).toISOString(), ticker: c.k?.trim() || null, chain: NETL[c.n] ?? null, mcap: c.m ? Math.round(c.m) : null,
  peak24h: Math.round(c.sim.pk24h * 100) / 100, r1h: Math.round(c.sim.r1h * 1000) / 1000, minutesToPeak: Math.round(c.sim.tPeakMin),
}));
const r0 = v => v == null || isNaN(v) ? null : Math.round(v);
const row = (t, p) => ({
  handle: t?.h ?? p.h, platform: (t?.platform ?? p.platform) === 'pump' ? 'Pump' : 'Fomo',
  style: t?.style ?? 'unrated', summary: t ? summary(t) : null,
  score: t?.score != null ? r3(t.score) : null, cut: t?.cutTier ?? null,
  calls: t?.calls ?? 0, alertsPerDay: t ? +t.alertsPerDay.toFixed(2) : 0,
  medianMcap: r0(t?.mcapMedian ?? p?.mcapMedian), capTier: capTier(t?.mcapMedian ?? p?.mcapMedian),
  chainFocus: chainFocus(p?.chains), speed: speed(t?.tPeakMed),
  hit50In15m: r3(t?.hit50in15m), hit2xIn24h: r3(t?.hit2xIn24h), stopOut30: r3(t?.sl30), medianMinutesToPeak: r0(t?.tPeakMed), avgPerCall: r3(t?.evAll),
  ownWinRate: r3(p?.platform === 'fomo' ? p?.ownWin : p?.exitedWin), medianHoldMinutes: r0(p?.platform === 'fomo' ? p?.holdMedianMin : null),
  realizedPnlUsd: r0(p?.realizedUsd),
  pnl30dUsd: r0(p?.pnlUsd_monthly), pnl7dUsd: r0(p?.pnlUsd_weekly), pnlRank30d: p?.pnlRank_monthly ?? null,
  portfolioPnlUsd: r0(p?.portfolioPnlUsd), callout2xRate: r3(p?.pumpCallout2x ?? p?.hist2x),
  followers: p?.followers ?? null, x: p?.x ?? null, wallet: p?.wallet ?? null,
  avatar: avatar.get(`${t?.platform ?? p.platform}|${t?.h ?? p.h}`) ?? null,
  recentCalls: recentCalls(`${t?.platform ?? p.platform}|${t?.h ?? p.h}`),
});
const seenKeys = new Set(tm.map(t => t.key));
const trackedKey = t => `${t.pl.toLowerCase()}|${t.h}`;
const intelTraders = [
  ...tm.map(t => row(t, t.profile)),
  // tracked traders with no calls in the window still get their public profile stats
  ...tradersDb.filter(t => !seenKeys.has(trackedKey(t)) && profBy.get(trackedKey(t))).map(t => row(null, profBy.get(trackedKey(t)))),
];
const appFile = { generatedAt: new Date().toISOString(), window: require('./window').label,
  levels: LEVELS, traders: intelTraders };
fs.writeFileSync(D + 'trader-intel.json', JSON.stringify(appFile));
fs.writeFileSync(D + 'intel.json', JSON.stringify({ pop, traders: tm, presets, levels, exclude: exclude.map(t => t.key), weak: weak.map(t => t.key), dormant }));

const cnt = tm.reduce((m, t) => (m[t.style] = (m[t.style] || 0) + 1, m), {});
console.log('styles', cnt, 'pop', Object.fromEntries(Object.entries(pop).map(([k, v]) => [k, v.toFixed(3)])));
for (const l of levels) console.log(l.name.padEnd(22), l._members.length, 'traders', l._perHour.toFixed(1), 'alerts/h', 'avgEv', l._avgEv?.toFixed(3));
console.log('intel rows', intelTraders.length, 'with 30d pnl', intelTraders.filter(t => t.pnl30dUsd != null).length, 'with followers', intelTraders.filter(t => t.followers != null).length);
console.log('exclude', exclude.length, 'weak', weak.length, 'dormant tracked', dormant.length);
