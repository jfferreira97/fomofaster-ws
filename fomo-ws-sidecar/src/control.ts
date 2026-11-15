import { createServer } from 'http';
import type { Page } from 'playwright';

// Local control server: the backend asks this sidecar to follow a trader with the FOMO account it is logged in as, so a trader a
// user adds on /manage starts reaching us right away instead of waiting for someone to follow them by hand. 127.0.0.1 only, and
// only started when CONTROL_PORT is set (the account-2 launcher sets it; the restricted account 1 never follows anyone).

export interface FollowResult {
  ok: boolean;
  /** followed | already | not_found | invalid | not_logged_in | rate_limited | busy | error */
  code: string;
  handle?: string;
  id?: string;
  message?: string;
}

// Runs inside the logged-in fomo.family page, the way the app itself follows someone: its API takes the session cookies
// (credentials: 'include', no bearer header on the web), and a 401 is answered by the app's own silent session refresh.
// Kept as a string: this project's tsconfig has no DOM lib.
// undo=true unfollows instead (DELETE /follows, the same body), for a trader added by mistake.
const FOLLOW_IN_PAGE = String.raw`async (handle, undo) => {
  const API = 'https://prod-api.fomo.family';
  const call = async (path, init) => {
    const r = await fetch(API + path, { credentials: 'include', ...(init || {}), headers: { 'Content-Type': 'application/json', 'App-Language': 'en' } });
    let b = null; try { b = await r.json(); } catch (e) { /* no body */ }
    return { status: b && typeof b.statusCode === 'number' ? b.statusCode : r.status, body: b };
  };
  const refresh = () => new Promise((resolve) => {
    const f = document.createElement('iframe');
    f.src = '/refresh?silent=1'; f.setAttribute('aria-hidden', 'true'); f.style.display = 'none';
    const done = (ok) => { window.removeEventListener('message', on); clearTimeout(t); f.remove(); resolve(ok); };
    const on = (e) => { if (e.origin === location.origin && e.source === f.contentWindow && e.data && e.data.type === 'privy-refresh-complete') done(!!e.data.success); };
    const t = setTimeout(() => done(false), 10000);
    window.addEventListener('message', on); document.body.appendChild(f);
  });
  let me = await call('/v2/users/current');
  if (me.status === 401) { await refresh(); me = await call('/v2/users/current'); }
  const meId = me.body && me.body.responseObject && me.body.responseObject.id;
  if (!meId) return { ok: false, code: 'not_logged_in', message: 'current user: HTTP ' + me.status };
  const u = await call('/v2/users/userHandle/' + encodeURIComponent(handle));
  const target = u.status === 200 && u.body ? u.body.responseObject : null;
  if (!target || !target.id) return { ok: false, code: u.status === 429 ? 'rate_limited' : u.status >= 500 ? 'error' : 'not_found', message: 'lookup: HTTP ' + u.status };
  const out = { handle: target.userHandle || handle, id: String(target.id) };
  if (out.id === String(meId)) return { ok: false, code: 'invalid', message: 'that is our own account', ...out };
  const ids = await call('/v2/users/current/followingIds');
  const list = ids.body && ids.body.responseObject && ids.body.responseObject.followingIds;
  const following = Array.isArray(list) && list.map(String).includes(out.id);
  if (undo) {
    if (Array.isArray(list) && !following) return { ok: true, code: 'not_following', ...out };
    const d = await call('/follows', { method: 'DELETE', body: JSON.stringify({ user_id: meId, following_id: target.id }) });
    if (d.status === 200) return { ok: true, code: 'unfollowed', ...out };
    return { ok: false, code: d.status === 429 ? 'rate_limited' : 'error', message: 'unfollow: HTTP ' + d.status, ...out };
  }
  if (following) return { ok: true, code: 'already', ...out };
  const f = await call('/follows', { method: 'POST', body: JSON.stringify({ user_id: meId, following_id: target.id }) });
  if (f.status === 200) return { ok: true, code: 'followed', ...out };
  return { ok: false, code: f.status === 429 ? 'rate_limited' : 'error', message: 'follow: HTTP ' + f.status, ...out };
}`;

export async function followOnFomo(page: Page, handle: string, undo = false): Promise<FollowResult> {
  return (await page.evaluate(`(${FOLLOW_IN_PAGE})(${JSON.stringify(handle)}, ${undo})`)) as FollowResult;
}

export interface LookupUser { handle: string; name?: string | null; pic?: string | null; followers?: number | null; verified?: boolean }
export interface LookupResult { ok: boolean; code: string; users: LookupUser[]; message?: string }

// The app's own user search box: GET /v2/users/fuzzy-search?searchTerm=<q> -> responseObject.users. Read-only.
const SEARCH_IN_PAGE = String.raw`async (q) => {
  const r = await fetch('https://prod-api.fomo.family/v2/users/fuzzy-search?searchTerm=' + encodeURIComponent(q), { credentials: 'include', headers: { 'Content-Type': 'application/json', 'App-Language': 'en' } });
  let b = null; try { b = await r.json(); } catch (e) { /* no body */ }
  const status = b && typeof b.statusCode === 'number' ? b.statusCode : r.status;
  if (status !== 200) return { ok: false, code: status === 401 ? 'not_logged_in' : status === 429 ? 'rate_limited' : 'error', users: [], message: 'search: HTTP ' + status };
  const list = (b.responseObject && b.responseObject.users) || [];
  const num = (v) => (typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' && !isNaN(+v) ? +v : null);
  return { ok: true, code: 'ok', fields: list[0] ? Object.keys(list[0]).join(',') : '', users: list.filter((u) => u && u.userHandle).slice(0, 10).map((u) => ({
    handle: u.userHandle,
    name: u.displayName || null,
    pic: u.profilePictureLink || null,
    followers: num(u.followerCount ?? u.followersCount ?? u.followers ?? u.numFollowers),
    verified: !!(u.verified || u.isVerified),
  })) };
}`;

export async function searchOnFomo(page: Page, q: string): Promise<LookupResult> {
  return (await page.evaluate(`(${SEARCH_IN_PAGE})(${JSON.stringify(q)})`)) as LookupResult;
}

const MIN_GAP_MS = 1_500;   // between two follows: one at a time, never a burst at the API
const LOOKUP_GAP_MS = 250;  // between two searches

export function startControlServer(port: number, getPage: () => Page | null, log: (m: string) => void): void {
  let queue: Promise<unknown> = Promise.resolve();
  let lastAt = 0;
  const follow = (handle: string, undo = false): Promise<FollowResult> => {
    const run = async (): Promise<FollowResult> => {
      const wait = lastAt + MIN_GAP_MS - Date.now();
      if (wait > 0) await new Promise((r) => setTimeout(r, wait));
      lastAt = Date.now();
      const page = getPage();
      if (!page) return { ok: false, code: 'busy', message: 'no page yet' };
      try {
        return await followOnFomo(page, handle, undo);
      } catch (err) {
        // the page was mid-navigation (recovery, rotation): the backend tells the user to try again
        return { ok: false, code: 'busy', message: String(err).slice(0, 200) };
      }
    };
    const p = queue.then(run, run);
    queue = p.catch(() => undefined);
    return p;
  };

  // Searches (the add form's suggestions as the user types) run in their own lane: never behind a follow, one at a time,
  // at least LOOKUP_GAP_MS apart. The backend caches the answers and the page waits for the user to stop typing.
  let lookups: Promise<unknown> = Promise.resolve();
  let lookupAt = 0;
  const lookup = (q: string): Promise<LookupResult> => {
    const run = async (): Promise<LookupResult> => {
      const wait = lookupAt + LOOKUP_GAP_MS - Date.now();
      if (wait > 0) await new Promise((r) => setTimeout(r, wait));
      lookupAt = Date.now();
      const page = getPage();
      if (!page) return { ok: false, code: 'busy', users: [] };
      try { return await searchOnFomo(page, q); } catch (err) { return { ok: false, code: 'busy', users: [], message: String(err).slice(0, 200) }; }
    };
    const p = lookups.then(run, run);
    lookups = p.catch(() => undefined);
    return p;
  };

  const server = createServer((req, res) => {
    const send = (status: number, body: unknown) => {
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(body));
    };
    if (req.method === 'GET' && req.url === '/health') return send(200, { ok: true, page: !!getPage() });
    if (req.method === 'GET' && req.url?.startsWith('/lookup?')) {
      const q = (new URL(req.url, 'http://localhost').searchParams.get('q') || '').trim().replace(/^@/, '');
      if (!/^[A-Za-z0-9_.-]{1,64}$/.test(q)) return send(400, { ok: false, code: 'invalid', users: [] });
      void lookup(q).then((r) => send(200, r));
      return;
    }
    if (req.method !== 'POST' || (req.url !== '/follow' && req.url !== '/unfollow')) return send(404, { ok: false, code: 'not_found_route' });
    const undo = req.url === '/unfollow';
    let raw = '';
    req.on('data', (c: Buffer) => { raw += c; if (raw.length > 4_096) req.destroy(); });
    req.on('end', () => {
      let handle = '';
      try { handle = String((JSON.parse(raw) as { handle?: unknown }).handle ?? '').trim().replace(/^@/, ''); } catch { /* bad json */ }
      if (!/^[A-Za-z0-9_.-]{1,64}$/.test(handle)) return send(400, { ok: false, code: 'invalid', message: 'not a handle' });
      void follow(handle, undo).then((r) => {
        log(`[control] ${undo ? 'unfollow' : 'follow'} ${handle}: ${r.code}${r.message ? ' (' + r.message + ')' : ''}`);
        send(200, r);
      });
    });
  });
  server.on('error', (err) => log(`[control] server error: ${String(err)}`));
  server.listen(port, '127.0.0.1', () => log(`[control] follow endpoint on 127.0.0.1:${port}`));
}
