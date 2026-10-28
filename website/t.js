/* GROUPCHAT first-party analytics. No cookies, honours Do Not Track.
   Sends: page view, clicks on the Telegram buttons, demo play, and which sections were reached.
   Every visit gets a short random token that is appended to the Telegram start link, so a trial can be tied back to the visit it came from.
   The owner can mark a browser as "ours" with ?internal=1 (and clear it with ?internal=0): those visits are kept out of the numbers. */
/* A referral code in the address (?r=code) is carried into the Telegram buttons as start=..._r_<code>. It is not analytics, so it runs even
   with Do Not Track. It is remembered for the browser, so the visitor can read other pages and still arrive with it. */
(function () {
  var code = '';
  try {
    var r = (new URLSearchParams(location.search).get('r') || '').toLowerCase().replace(/[^a-z0-9]/g, '').slice(0, 16);
    if (r.length >= 3) { code = r; try { localStorage.setItem('gc_r', r); } catch (e) {} }
    else { try { code = localStorage.getItem('gc_r') || ''; } catch (e) {} }
  } catch (e) {}
  if (code.length < 3) return;
  document.querySelectorAll('a[href*="t.me/GROUPCHAT_ALERTS_BOT"]').forEach(function (a) {
    a.href = a.href.replace(/start=([A-Za-z0-9_\-]+)/, function (m, s) { return 'start=' + s.slice(0, 30) + '_r_' + code; });
    if (!/start=/.test(a.href)) a.href += (a.href.indexOf('?') < 0 ? '?' : '&') + 'start=r_' + code;
  });
})();

(function () {
  try { if (navigator.doNotTrack === '1' || window.doNotTrack === '1' || navigator.globalPrivacyControl) return; } catch (e) {}
  var q = new URLSearchParams(location.search);
  var utm = (q.get('utm_source') || q.get('ref') || '').replace(/[^A-Za-z0-9_.\-]/g, '').slice(0, 24);

  var internal = 0;
  try {
    if (q.get('internal') === '1') localStorage.setItem('gc_internal', '1');
    if (q.get('internal') === '0') localStorage.removeItem('gc_internal');
    internal = localStorage.getItem('gc_internal') === '1' ? 1 : 0;
  } catch (e) {}

  var token = '';
  try {
    var a = new Uint8Array(8); crypto.getRandomValues(a);
    var cs = 'abcdefghijklmnopqrstuvwxyz0123456789';
    for (var i = 0; i < a.length; i++) token += cs[a[i] % cs.length];
  } catch (e) { token = Math.random().toString(36).slice(2, 10); }

  function send(t, l) {
    var body = JSON.stringify({ t: t, p: location.pathname, l: l || '', r: document.referrer || '', u: utm, k: token, i: internal });
    try { if (navigator.sendBeacon && navigator.sendBeacon('/api/track', new Blob([body], { type: 'application/json' }))) return; } catch (e) {}
    try { fetch('/api/track', { method: 'POST', body: body, headers: { 'Content-Type': 'application/json' }, keepalive: true }); } catch (e) {}
  }

  // Carry the traffic source and the visit token into the bot's /start payload (web_hero -> web_hero_u_x_v_ab12cd34), within Telegram's 64 characters.
  var suffix = (utm ? '_u_' + utm : '') + '_v_' + token;
  document.querySelectorAll('a[href*="t.me/GROUPCHAT_ALERTS_BOT"]').forEach(function (a) {
    a.href = a.href.replace(/start=([A-Za-z0-9_\-]+)/, function (m, s) {
      var keep = 64 - ('_v_' + token).length;
      var base = (s + (utm ? '_u_' + utm : '')).slice(0, keep);
      return 'start=' + base + '_v_' + token;
    });
  });

  send('view');

  // The app pages report what is done on them through this (page-wide counts, no identity). Only exists when DNT/GPC allow tracking.
  window.gcEvent = function (l) { send('feed', l); };

  // Clicks from a site page to the app: which link (Feed/Manage) and where on the page it sat.
  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[href="/feed"],a[href="/manage"]');
    if (!a) return;
    var where = a.closest('footer') ? 'footer' : a.closest('header,.topbar') ? 'top' : 'body';
    send('nav', a.getAttribute('href').slice(1) + '_' + where);
  }, true);

  document.addEventListener('click', function (e) {
    var a = e.target.closest && e.target.closest('a[href*="t.me/GROUPCHAT_ALERTS_BOT"]');
    if (!a) return;
    var m = /start=([A-Za-z0-9_\-]+)/.exec(a.href);
    send('cta', m ? m[1].split('_v_')[0].split('_u_')[0] : 'direct');
  }, true);

  var play = document.getElementById('playbtn');
  if (play) play.addEventListener('click', function () { send('play', 'demo'); });

  if ('IntersectionObserver' in window) {
    var seen = {};
    var io = new IntersectionObserver(function (es) {
      es.forEach(function (en) {
        if (en.isIntersecting && !seen[en.target.id]) { seen[en.target.id] = 1; send('sec', en.target.id); io.unobserve(en.target); }
      });
    }, { threshold: 0.35 });
    ['speed', 'compare', 'traders', 'pricing', 'faq'].forEach(function (id) { var el = document.getElementById(id); if (el) io.observe(el); });
  }
})();
