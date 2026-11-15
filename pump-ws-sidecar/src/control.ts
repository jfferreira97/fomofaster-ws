import { createServer } from 'http';
import type { Page } from 'playwright';

// Local control server: the backend asks this sidecar to follow a trader with the pump.fun account it is logged in as, so a
// trader a user adds on /manage starts reaching us right away instead of waiting for someone to follow them by hand.
// 127.0.0.1 only. CONTROL_PORT picks the port (default 8142), CONTROL_PORT=0 turns it off.

export interface FollowResult {
  ok: boolean;
  /** followed | already | not_found | invalid | not_logged_in | rate_limited | busy | error */
  code: string;
  handle?: string;
  id?: string;
  message?: string;
}

// Runs inside the logged-in pump.fun page (plain HTTP from this machine gets Cloudflare's 403). A handle or a wallet both work:
// /users/<x> answers either. The follow is the same POST /following/v2/<wallet> the site's Follow button sends.
// Kept as a string: this project's tsconfig has no DOM lib.
// undo=true unfollows instead (DELETE /following/<wallet>), for a trader added by mistake.
const FOLLOW_IN_PAGE = String.raw`async (input, undo) => {
  const API = 'https://frontend-api-v3.pump.fun';
  const get = async (path) => {
    const r = await fetch(API + path, { credentials: 'include' });
    let b = null; try { b = await r.json(); } catch (e) { /* no body */ }
    return { status: r.status, body: b };
  };
  const me = await get('/auth/my-profile');
  const myWallet = me.body && me.body.address;
  if (me.status !== 200 || !myWallet) return { ok: false, code: 'not_logged_in', message: 'my-profile: HTTP ' + me.status };
  const u = await get('/users/' + encodeURIComponent(input));
  const user = u.status === 200 ? u.body : null;
  const wallet = user && (user.address || user.canonical_svm_wallet);
  if (!wallet) return { ok: false, code: u.status === 429 ? 'rate_limited' : u.status >= 500 ? 'error' : 'not_found', message: 'lookup: HTTP ' + u.status };
  const name = user.username || user.userName || null;
  const out = { handle: name || input, id: wallet };
  if (!name) return { ok: false, code: 'invalid', message: 'that account has no pump.fun username', ...out };
  if (wallet === myWallet) return { ok: false, code: 'invalid', message: 'that is our own account', ...out };
  const s = await get('/following/single/' + wallet + '?userId=' + myWallet);
  const following = s.status === 200 && !!(s.body && s.body.follow);
  if (undo) {
    if (s.status === 200 && !following) return { ok: true, code: 'not_following', ...out };
    const d = await fetch(API + '/following/' + wallet, { method: 'DELETE', credentials: 'include' });
    if (d.status === 200 || d.status === 204) return { ok: true, code: 'unfollowed', ...out };
    return { ok: false, code: d.status === 429 ? 'rate_limited' : 'error', message: 'unfollow: HTTP ' + d.status, ...out };
  }
  if (following) return { ok: true, code: 'already', ...out };
  const f = await fetch(API + '/following/v2/' + wallet, { method: 'POST', credentials: 'include' });
  if (f.status === 200 || f.status === 201) return { ok: true, code: 'followed', ...out };
  return { ok: false, code: f.status === 429 ? 'rate_limited' : 'error', message: 'follow: HTTP ' + f.status, ...out };
}`;

export async function followOnPump(page: Page, input: string, undo = false): Promise<FollowResult> {
  return (await page.evaluate(`(${FOLLOW_IN_PAGE})(${JSON.stringify(input)}, ${undo})`)) as FollowResult;
}

export interface LookupUser { handle: string; name?: string | null; pic?: string | null; followers?: number | null; verified?: boolean }
export interface LookupResult { ok: boolean; code: string; users: LookupUser[]; message?: string }

// pump.fun has no user search (only coin search), so this is the exact check: GET /users/<handle or wallet>, the same lookup
// a profile page makes. 404 = no such account. Read-only.
const LOOKUP_IN_PAGE = String.raw`async (q) => {
  const r = await fetch('https://frontend-api-v3.pump.fun/users/' + encodeURIComponent(q), { credentials: 'include' });
  if (r.status === 404 || r.status === 400) return { ok: true, code: 'ok', users: [] };
  if (r.status !== 200) return { ok: false, code: r.status === 429 ? 'rate_limited' : 'error', users: [], message: 'lookup: HTTP ' + r.status };
  let u = null; try { u = await r.json(); } catch (e) { /* no body */ }
  const handle = u && (u.username || u.userName);
  if (!handle) return { ok: true, code: 'ok', users: [] };
  const num = (v) => (typeof v === 'number' ? v : typeof v === 'string' && v.trim() !== '' && !isNaN(+v) ? +v : null);
  return { ok: true, code: 'ok', fields: Object.keys(u).join(','), users: [{
    handle,
    name: null,
    pic: u.profile_image || u.profileImage || null,
    followers: num(u.followers ?? u.followerCount ?? u.followers_count),
    verified: !!(u.isVerified || u.verified),
  }] };
}`;

export async function lookupOnPump(page: Page, q: string): Promise<LookupResult> {
  return (await page.evaluate(`(${LOOKUP_IN_PAGE})(${JSON.stringify(q)})`)) as LookupResult;
}

const MIN_GAP_MS = 1_500;   // between two follows: one at a time, never a burst at the API (it is the same IP as the alerts poll)
const LOOKUP_GAP_MS = 400;  // between two lookups (same IP as the alerts poll: gentle)

export function startControlServer(port: number, getPage: () => Page | null, log: (m: string) => void): void {
  let queue: Promise<unknown> = Promise.resolve();
  let lastAt = 0;
  const follow = (input: string, undo = false): Promise<FollowResult> => {
    const run = async (): Promise<FollowResult> => {
      const wait = lastAt + MIN_GAP_MS - Date.now();
      if (wait > 0) await new Promise((r) => setTimeout(r, wait));
      lastAt = Date.now();
      const page = getPage();
      if (!page) return { ok: false, code: 'busy', message: 'no page yet' };
      try {
        return await followOnPump(page, input, undo);
      } catch (err) {
        // the page was mid-navigation (recovery, rotation): the backend tells the user to try again
        return { ok: false, code: 'busy', message: String(err).slice(0, 200) };
      }
    };
    const p = queue.then(run, run);
    queue = p.catch(() => undefined);
    return p;
  };

  // Lookups (the add form checking a handle as the user types) run in their own lane: never behind a follow, one at a time,
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
      try { return await lookupOnPump(page, q); } catch (err) { return { ok: false, code: 'busy', users: [], message: String(err).slice(0, 200) }; }
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
      let input = '';
      try { input = String((JSON.parse(raw) as { handle?: unknown }).handle ?? '').trim().replace(/^@/, ''); } catch { /* bad json */ }
      if (!/^[A-Za-z0-9_.-]{1,64}$/.test(input)) return send(400, { ok: false, code: 'invalid', message: 'not a handle or wallet' });
      void follow(input, undo).then((r) => {
        log(`[control] ${undo ? 'unfollow' : 'follow'} ${input}: ${r.code}${r.message ? ' (' + r.message + ')' : ''}`);
        send(200, r);
      });
    });
  });
  server.on('error', (err) => log(`[control] server error: ${String(err)}`));
  server.listen(port, '127.0.0.1', () => log(`[control] follow endpoint on 127.0.0.1:${port}`));
}
