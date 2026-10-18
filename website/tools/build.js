// Regenerates the public site from src/ templates using live facts from the bot database.
//   node build.js            build once
//   node build.js --watch    keep it current: price + trader count every 30 s, speed figures every 60 min
// Facts: price = AppConfigs.SubscriptionPriceSol (the setting the dashboard edits), trader count = Traders table,
// speed = time from an event reaching the backend to the first Telegram message, over the last 7 days.
const { execFileSync } = require('child_process');
const fs = require('fs');
const SITE = process.env.SITE_OUT || require('path').resolve(__dirname, '..').split('\\').join('/');
const SRC = SITE + '/src';
const STATE = SITE + '/tools/facts.json';
const DB = process.env.SITE_DB || 'C:/apps/fomofaster-ws/telegram-bot/TelegramBot/fomofaster_ws.db';
const SQ = 'C:/apps/tools/sqlite3.exe';
const FILES = ['index.html', 'terms.html', 'fomo-trader-alerts.html', 'pump-fun-callouts-alerts.html', 'how-we-score-traders.html', 'fomo-app-delay.html', 'llms.txt'];
const log = (...a) => console.log(new Date().toISOString().slice(0, 19).replace('T', ' '), '[site]', ...a);

function q(sql) {
  return execFileSync(SQ, ['-readonly', '-separator', '|', DB, '.timeout 20000', sql], { encoding: 'utf8', maxBuffer: 128 * 1024 * 1024 }).trim();
}
const num = (v) => (v === '' ? NaN : Number(v));
const pct = (arr, p) => { const s = [...arr].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(s.length * p))]; };
const ts = (str) => Date.parse(str.trim().replace(' ', 'T').replace(/(\.\d{3})\d*/, '$1') + 'Z');
const fmtInt = (n) => n.toLocaleString('en-US');

function cheapFacts() {
  const rawPrice = num(q("select Value from AppConfigs where Key='SubscriptionPriceSol'"));
  if (!(rawPrice > 0)) throw new Error('SubscriptionPriceSol missing or invalid');
  const traders = num(q('select count(*) from Traders'));
  return { PRICE: String(+rawPrice.toFixed(3)), TRADERS: fmtInt(Math.floor(traders / 100) * 100) + '+' };
}

function speedFacts() {
  // FOMO: event received -> first Telegram message, last 7 days
  const rows = q(`select (julianday(m.f)-julianday(e.ReceivedAt))*86400
    from Notifications n join WsEvents e on e.WsId=n.FK_WsEvent_WsId
    join (select NotificationId, min(SentAt) f from SentMessages where SentAt > datetime('now','-8 days') group by NotificationId) m on m.NotificationId=n.Id
    where n.SentAt > datetime('now','-7 days');`).split('\n').map(Number).filter((v) => isFinite(v) && v >= 0 && v < 120);
  if (rows.length < 300) throw new Error('only ' + rows.length + ' FOMO samples');
  const med = pct(rows, 0.5), p90 = pct(rows, 0.9), under1 = rows.filter((v) => v < 1).length / rows.length;
  const edges = [0, 0.25, 0.5, 0.75, 1, 2, 5, 1e9];
  const buckets = edges.slice(0, -1).map((lo, i) => +(100 * rows.filter((v) => v >= lo && v < edges[i + 1]).length / rows.length).toFixed(1));

  // Pump: match each Pump notification to the latest Pump event received before it (notifications carry no event id)
  const ev = q('select ReceivedAt from PumpEvents where Id > (select max(Id)-4000 from PumpEvents) order by Id;').split('\n').map(ts).filter(isFinite).sort((a, b) => a - b);
  const nt = q(`select n.SentAt, (select min(SentAt) from SentMessages where NotificationId=n.Id) from Notifications n
    where n.Platform='Pump' and n.Id > (select max(Id)-4000 from Notifications) and n.SentAt > datetime('now','-7 days');`)
    .split('\n').filter(Boolean).map((l) => { const c = l.split('|'); return { sent: ts(c[0]), first: c[1] ? ts(c[1]) : NaN }; });
  const pd = [];
  for (const n of nt) {
    if (!isFinite(n.sent) || !isFinite(n.first)) continue;
    let lo = 0, hi = ev.length - 1, best = -1;
    while (lo <= hi) { const m = (lo + hi) >> 1; if (ev[m] <= n.sent) { best = m; lo = m + 1; } else hi = m - 1; }
    if (best >= 0) { const d = (n.first - ev[best]) / 1000; if (d >= 0 && d < 120) pd.push(d); }
  }
  if (pd.length < 100) throw new Error('only ' + pd.length + ' Pump samples');
  const pMed = pct(pd, 0.5), pUnder1 = pd.filter((v) => v < 1).length / pd.length;

  // Safety: only publish "under a second" figures when they are really true.
  if (med > 1 || under1 < 0.8 || pMed > 1 || pUnder1 < 0.8) throw new Error(`speed no longer supports the claim (median ${med.toFixed(2)}, under1 ${(under1 * 100).toFixed(0)}%, pump ${pMed.toFixed(2)}/${(pUnder1 * 100).toFixed(0)}%)`);
  return {
    MED1: med.toFixed(1), MED2: med.toFixed(2), P90: p90.toFixed(1), UNDER1: String(Math.round(under1 * 100)), SLOWER: String(100 - Math.round(under1 * 100)),
    N: fmtInt(rows.length), BUCKETS: JSON.stringify(buckets), PUMP_MED2: pMed.toFixed(2), PUMP_UNDER1: String(Math.round(pUnder1 * 100)), SPEED_AT: Date.now(),
  };
}

function load() { try { return JSON.parse(fs.readFileSync(STATE, 'utf8')); } catch { return {}; } }

function bucketRows(facts) {
  const labels = ['0 to 0.25 s', '0.25 to 0.5 s', '0.5 to 0.75 s', '0.75 to 1 s', '1 to 2 s', '2 to 5 s', '5 s or more'];
  const b = JSON.parse(facts.BUCKETS);
  return labels.map((l, i) => '<tr><th scope="row">' + l + '</th><td>' + b[i] + '%</td></tr>').join('');
}

function render(facts) {
  facts = Object.assign({}, facts, { BUCKET_ROWS: bucketRows(facts) });
  let changed = 0;
  for (const f of FILES) {
    let s = fs.readFileSync(`${SRC}/${f}`, 'utf8');
    s = s.replace(/\{\{([A-Z0-9_]+)\}\}/g, (m, k) => { if (!(k in facts)) throw new Error(`${f}: no value for {{${k}}}`); return facts[k]; });
    const out = `${SITE}/${f}`;
    const prev = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : '';
    if (prev !== s) { fs.writeFileSync(out, s); changed++; }
  }
  return changed;
}

function step(forceSpeed) {
  const facts = Object.assign({}, load());
  Object.assign(facts, cheapFacts());
  const stale = !facts.SPEED_AT || Date.now() - facts.SPEED_AT > 60 * 60 * 1000;
  if (forceSpeed || stale) {
    try { Object.assign(facts, speedFacts()); log('speed refreshed:', facts.MED2 + 's median,', facts.UNDER1 + '% under 1 s, n=' + facts.N); }
    catch (e) { log('speed NOT refreshed, keeping the last good figures:', e.message); if (!facts.MED1) throw e; }
  }
  fs.writeFileSync(STATE, JSON.stringify(facts, null, 1));
  const n = render(facts);
  if (n) log(`rewrote ${n} file(s): price ${facts.PRICE} SOL, traders ${facts.TRADERS}`);
}

if (process.argv.includes('--watch')) {
  step(true);
  setInterval(() => { try { step(false); } catch (e) { log('error:', e.message); } }, 30000);
  log('watching');
} else { step(true); log('built once'); }
