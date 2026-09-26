// Renders report/trader-audit.html from data/intel.json (+ profiles, wallets, calls).
const fs = require('fs');
const D = __dirname + '/data/';
const intel = JSON.parse(fs.readFileSync(D + 'intel.json'));
const calls = JSON.parse(fs.readFileSync(D + 'call_metrics.json'));
const wallets = fs.existsSync(D + 'fomo_wallets.json') ? JSON.parse(fs.readFileSync(D + 'fomo_wallets.json')) : {};
const prof = JSON.parse(fs.readFileSync(D + 'profiles.json'));
const profBy = new Map(prof.map(p => [`${p.platform}|${p.h}`, p]));

const r2 = v => v == null || isNaN(v) ? null : Math.round(v * 1000) / 1000;
const r0 = v => v == null || isNaN(v) ? null : Math.round(v);
const rows = intel.traders.map(t => {
  const p = profBy.get(t.key) || {};
  const w = wallets[t.h] || {};
  return {
    k: t.key, h: t.h, pl: t.platform, st: t.style, n: t.calls, ns: t.simulated, apd: r2(t.alertsPerDay),
    mc: r0(t.mcapMedian), micro: r2(t.microShare),
    ev: r2(t.evAll), best: r2(t.bestEv), strat: t.bestStrat || null, score: r2(t.score), tier: t.cutTier || null, evS: r2(t.evScalp), evF: r2(t.evFlip), evR: r2(t.evRunner),
    h50: r2(t.hit50in15m), h2: r2(t.hit2xIn24h), h3: r2(t.hit3xIn24h), sl30: r2(t.sl30), sl40: r2(t.sl40), fp: r2(t.fp50_30),
    tpk: r0(t.tPeakMed), late: r2(t.entryVsCallMed),
    ownW: r2(t.platform === 'fomo' ? p.ownWin : p.exitedWin), hold: r0(p.holdMedianMin), dump: r2(p.fastDump),
    rt: p.roundTrips ?? null, real: r0(p.realizedUsd), eq: r0(p.equity), pnl: r0(p.pnlTotalUsd),
    hist: p.histCallouts ?? null, h2h: r2(p.hist2x), h5h: r2(p.hist5x), bestCall: r2(p.bestCalloutMult), lb10: p.lbWeeklyTop10 ?? null, lbBest: p.lbBestRank ?? null,
    rankM: p.pnlRank_monthly ?? null, fol: p.followers ?? null, cs2x: r2(p.pumpCallout2x ?? p.hist2x), port: r0(p.portfolioPnlUsd ?? p.pnlTotalUsd), pnlM: r0(p.pnlUsd_monthly), pnlW: r0(p.pnlUsd_weekly), x: p.x ?? null, wSol: w.sol || p.wallet || null, wEvm: w.evm || null,
    th: p.thesisCount ?? null, thMc: r0(p.thesisMcapMedian),
    why: t.reasons || [],
  };
});
const all = rows;
const simulated = calls.filter(c => c.sim).length;
const data = {
  generated: new Date().toISOString(), window: require('./window').label, halfA: require('./window').halfA, halfB: require('./window').halfB, totalCalls: calls.length, simulated,
  pop: intel.pop, rows: all,
  presets: [],
  levels: intel.levels.map(l => ({ id: l.id, name: l.name, description: l.description, budget: l.alertsPerHour, aph: +l._perHour.toFixed(1), ev: r2(l._avgEv), members: l._members })),
  platform: fs.existsSync(D + 'research_platform.json') ? JSON.parse(fs.readFileSync(D + 'research_platform.json')) : null,
  alerts: fs.existsSync(D + 'research_alerts.json') ? JSON.parse(fs.readFileSync(D + 'research_alerts.json')) : null,
  fanout: (() => { try { const d = JSON.parse(fs.readFileSync(D + 'send_delay.json')); const by = {}; for (const x of d) (by[x.nid] ||= []).push(x.d); const q = (a, p) => a.sort((x, y) => x - y)[Math.floor(p * a.length)]; const v = Object.values(by); return { first: q(v.map(a => Math.min(...a)), .5), last: q(v.map(a => Math.max(...a)), .5), lastP90: q(v.map(a => Math.max(...a)), .9), recipients: q(v.map(a => a.length), .5) }; } catch { return null; } })(),
  ingest: (() => { try { const d = JSON.parse(fs.readFileSync(D + 'ingest_delay.json')); const q = (a, p) => a.sort((x, y) => x - y)[Math.floor(p * a.length)]; const f = pl => d.filter(x => x.pl === pl).map(x => x.d); return { fomo: q(f('fomo'), .5), fomoP90: q(f('fomo'), .9), pump: q(f('pump'), .5), pumpP90: q(f('pump'), .9) }; } catch { return null; } })(),
  exclude: intel.exclude, weak: intel.weak,
  dormant: intel.dormant.map(t => ({ h: t.h, pl: t.pl })),
  validation: JSON.parse(fs.readFileSync(D + 'score_validation.json')), persistence: JSON.parse(fs.readFileSync(D + 'persistence.json')),
  coverage: JSON.parse(fs.readFileSync(D + 'coverage.json')),
};
const tpl = fs.readFileSync(__dirname + '/report_template.html', 'utf8');
fs.mkdirSync(__dirname + '/report', { recursive: true });
fs.writeFileSync(__dirname + '/report/trader-audit.html', tpl.replace('/*__DATA__*/null', JSON.stringify(data).replace(/</g, '\\u003c')));
console.log('report written', (fs.statSync(__dirname + '/report/trader-audit.html').size / 1024).toFixed(0) + 'KB', 'rows', all.length);
