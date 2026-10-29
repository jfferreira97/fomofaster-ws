import type { Page } from 'playwright';
import { appendFile } from 'fs';
import { join } from 'path';

// pump.fun's own live path for the "following" alerts feed, copied from its frontend:
//   1. POST /following-positions/alerts/presence registers this viewer and returns the exact NATS
//      subject to consume plus the heartbeat cadence ("call this every heartbeatIntervalSeconds").
//      The server publishes stream events ONLY for viewers with live presence.
//   2. The page's own NATS Web Worker is told to subscribe to that subject on the CORE instance
//      ({type:'subscribe', instance:'CORE', channel:<subject>}), exactly as useNatsSubscriptionHandler does.
//   3. Events arrive as NATS frames on the page's nats.realtime.pump.fun websocket. For callouts, replies
//      and reposts the event is only a doorbell ("a new row exists"): the site then re-fetches the feed.
//      Trades are inserted straight from the event; we do not ingest trades, so they are ignored upstream.
// The logged-in page sets the stream's login up for its own worker, and this listens to what the page itself
// receives (same approach as bag-watch's pump trades tab). natsMirror.ts adds connections from Node with what
// the page hands its worker, because one connection can land on a server that delivers nothing.
// The stream "degrades to HTTP polling, never the reverse" (site contract), so index.ts keeps a slow
// fallback poll and speeds it up only while the stream is not healthy.

const API = 'https://frontend-api-v3.pump.fun';
const PRESENCE_URL = `${API}/following-positions/alerts/presence`;
const NATS_WS = /nats\.realtime\.pump\.fun/;
const NATS_INSTANCE = 'CORE';
// Same band the HTTP feed used (see ALERTS_API in index.ts).
const PRESENCE_FILTERS = { minTradeAmountUsd: 10 };

export interface StreamEvent {
  kind: string;
  id: string;
  createdAt?: string;
  receivedAtMs: number;
  /** the full parsed event (trades are self-contained; the other kinds are only a doorbell) */
  raw: unknown;
}

export interface AlertStream {
  /** Start registering presence (call once the session is logged in; listening starts at creation). */
  begin(): void;
  healthy(): boolean;
  /** Listening for real: presence is alive, the subject is subscribed and the NATS socket is delivering frames right now. */
  ready(): boolean;
  /** The subject pattern the presence call returned (null until it did, or during a registry outage). */
  subject(): string | null;
  describe(): string;
  stop(): void;
}

interface PresenceResult {
  ok: boolean;
  status?: number;
  error?: string;
  data?: { subject: string | null; heartbeatIntervalSeconds?: number; presenceTtlSeconds?: number };
}

export function startAlertStream(
  page: Page,
  onEvent: (ev: StreamEvent) => void,
  log: (msg: string) => void,
): AlertStream {
  let subject: string | null = null;
  let subscribedSubject: string | null = null;
  let hbSeconds = 60;
  let lastPresenceOkAt = 0;
  let lastFrameAt = 0;
  let lastEventAt = 0;
  let natsSocketSeen = false;
  let frames = 0;
  let events = 0;
  let presenceFailures = 0;
  let stopped = false;
  let begun = false;
  let timer: NodeJS.Timeout | undefined;

  // Why might the live stream be silent? The CORE instance needs a per-user NATS JWT that pump.fun's own page
  // mints (POST nats/token) and hands to its worker. Log what that call returned (status + error code only,
  // never the JWT) and any NATS "-ERR" (e.g. permissions violation) so the silence has a reason in the log.
  let errLogs = 0;
  page.on('response', (r) => {
    if (!/\/nats\/token(\?|$)/.test(r.url())) return;
    const status = r.status();
    if (status >= 200 && status < 300) { log(`[stream] page minted its NATS token ok (HTTP ${status})`); return; }
    void r.json().then((j: unknown) => {
      const code = (j as { error?: unknown } | null)?.error;
      log(`[stream] page's NATS token mint FAILED: HTTP ${status} ${typeof code === 'string' ? code : ''}`);
    }).catch(() => log(`[stream] page's NATS token mint FAILED: HTTP ${status}`));
  });

  // Drain what the page <-> worker exchange (types only, see the init script in index.ts) into the log.
  const wlogTimer = setInterval(() => {
    page.evaluate(`(() => { const a = window.__wlog || []; window.__wlog = []; return JSON.stringify(a); })()`)
      .then((s) => {
        const arr = JSON.parse(String(s)) as Array<Record<string, unknown>>;
        // Every batch of events is an "in messages" plus an "out ack" frame: ~8 lines a second of nothing. Only the
        // rest (subscribe, status, errors, reconnects) is worth a line.
        for (const o of arr.filter((x) => x.type !== 'ack' && x.type !== 'messages')) log(`[worker] ${Object.entries(o).filter(([, v]) => v !== undefined && v !== '').map(([k, v]) => `${k}=${v}`).join(' ')}`);
      })
      .catch(() => { /* page navigating */ });
  }, 5_000);

  // Which subjects actually arrive on which NATS socket (first two tokens only, user ids masked), summarised every 30s.
  const seenSubjects = new Map<string, Map<string, number>>();
  const maskSubject = (subj: string) => subj.split('.').map((p, i) => (i < 2 ? p : p.length > 8 ? p.slice(0, 3) + '~' : p)).join('.');
  function tally(label: string, s: string): void {
    for (const m of s.matchAll(/^MSG (\S+) /gm)) {
      const key = maskSubject(m[1]).split('.').slice(0, 3).join('.');
      const per = seenSubjects.get(label) ?? new Map<string, number>();
      per.set(key, (per.get(key) ?? 0) + 1);
      seenSubjects.set(label, per);
    }
  }
  const tallyTimer = setInterval(() => {
    for (const [label, per] of seenSubjects) {
      const top = [...per.entries()].sort((a, b) => b[1] - a[1]).slice(0, 6).map(([k, v]) => `${k}=${v}`).join(' ');
      log(`[stream] ${label} subjects (last 30s): ${top}`);
    }
    seenSubjects.clear();
  }, 30_000);

  const seenWsHosts = new Set<string>();
  let openSockets = 0;
  page.on('websocket', (ws) => {
    // The page re-opens its Solana RPC socket about once a second; once per host is enough to know it exists.
    try { const u = new URL(ws.url()); if (!seenWsHosts.has(u.host)) { seenWsHosts.add(u.host); log(`[stream] websocket ${u.host}${u.pathname}`); } } catch { /* not a url */ }
    if (!NATS_WS.test(ws.url())) return;
    openSockets++;
    natsSocketSeen = true;
    log(`[stream] nats websocket open`);
    // a new socket means the worker reconnected: subscriptions live in the worker, so re-send ours
    subscribedSubject = null;
    if (begun) { if (timer) clearTimeout(timer); timer = setTimeout(() => { void beat(); }, 3_000); }
    const label = (() => { try { return new URL(ws.url()).host.split('.')[0]; } catch { return 'nats'; } })();
    ws.on('framereceived', (f) => { const s = String(f.payload); tally(label, s); onFrame(s); });
    ws.on('close', () => { openSockets = Math.max(0, openSockets - 1); natsSocketSeen = openSockets > 0; log(`[stream] nats websocket closed (${openSockets} still open)`); });
  });

  // The presence call returns a NATS subject PATTERN ("alertsFeed.user.<uid>.*"): '*' is one token, '>' the rest.
  // Delivered messages carry the concrete subject, so compare with NATS wildcard rules, not text equality.
  function subjectMatches(pattern: string, actual: string): boolean {
    const p = pattern.split('.'), a = actual.split('.');
    for (let i = 0; i < p.length; i++) {
      if (p[i] === '>') return a.length > i;
      if (i >= a.length) return false;
      if (p[i] !== '*' && p[i] !== a[i]) return false;
    }
    return p.length === a.length;
  }
  let samples = 0;

  function onFrame(s: string): void {
    lastFrameAt = Date.now();
    frames++;
    if (s.includes('-ERR') && errLogs < 20) { errLogs++; log(`[stream] NATS error frame: ${s.replace(/\s+/g, ' ').slice(0, 160)}`); }
    if (!subject) return;
    const prefix = subject.split(/[*>]/)[0];          // e.g. "alertsFeed.user.<uid>."
    if (!s.includes(prefix)) return;
    // NATS text protocol: "MSG <subject> <sid> [reply] <len>\r\n<payload>\r\n", several may share a frame.
    const lines = s.split(/\r?\n/);
    for (let i = 0; i < lines.length - 1; i++) {
      const m = /^MSG (\S+) \S+(?: \S+)? \d+$/.exec(lines[i]);
      if (!m || !subjectMatches(subject, m[1])) continue;
      if (samples < 25) { samples++; try { appendFile(join(__dirname, '..', 'logs', 'stream-event-samples.jsonl'), JSON.stringify({ at: new Date().toISOString(), subject: maskSubject(m[1]), payload: lines[i + 1].slice(0, 4000) }) + '\n', () => { /* best effort */ }); } catch { /* best effort */ } }
      let j: unknown;
      try { j = JSON.parse(lines[i + 1]); if (typeof j === 'string') j = JSON.parse(j); } catch { continue; }
      i++;
      const e = j as { kind?: unknown; id?: unknown; createdAt?: unknown };
      if (!e || typeof e.kind !== 'string' || typeof e.id !== 'string') continue;
      events++;
      lastEventAt = Date.now();
      onEvent({ kind: e.kind, id: e.id, createdAt: typeof e.createdAt === 'string' ? e.createdAt : undefined, receivedAtMs: Date.now(), raw: j });
    }
  }

  // browser-side code as a string: this project's tsconfig has no DOM lib
  const postToWorker = (msg: object): Promise<boolean> =>
    page.evaluate(
      `(() => { const w = window.__workers && window.__workers[0]; if (!w) return false; (w.__rawPost || w.postMessage.bind(w))(${JSON.stringify(msg)}); return true; })()`,
    ).then(Boolean).catch(() => false);

  // pump.fun's own page subscribes to this user's alerts subject on CORE by itself (its feed component does
  // it at load), so there is normally nothing to add: a second subscription would only duplicate every event.
  const pageSubscribedAlerts = (): Promise<boolean> =>
    page.evaluate('!!window.__pageAlertsSub').then(Boolean).catch(() => false);

  // A body-less beat "only renews liveness and preserves the stored bands" (pump.fun's API notes): the page's own
  // feed component owns the bands, so do not fight it. If a bare beat is refused, fall back to stating our bands.
  async function registerPresence(): Promise<PresenceResult> {
    const call = (body: string | null) => page.evaluate(async ({ url, body }) => {
      try {
        const res = await fetch(url, { method: 'POST', credentials: 'include', headers: body ? { 'content-type': 'application/json' } : {}, ...(body ? { body } : {}) });
        if (!res.ok) return { ok: false, status: res.status };
        return { ok: true, data: await res.json() };
      } catch (e) {
        return { ok: false, error: String(e) };
      }
    }, { url: PRESENCE_URL, body }) as Promise<PresenceResult>;
    const bare = await call(null);
    if (bare.ok && bare.data?.subject) return bare;
    return call(JSON.stringify({ filters: PRESENCE_FILTERS }));
  }

  async function beat(): Promise<void> {
    if (stopped) return;
    try {
      const r = await registerPresence();
      if (r.ok && r.data) {
        presenceFailures = 0;
        lastPresenceOkAt = Date.now();
        if (r.data.heartbeatIntervalSeconds) hbSeconds = r.data.heartbeatIntervalSeconds;
        const next = r.data.subject ?? null;
        if (next !== subject) {
          if (subject && subscribedSubject === subject) {
            await postToWorker({ type: 'unsubscribe', instance: NATS_INSTANCE, channel: subject });
          }
          subject = next;
          subscribedSubject = null;
          log(`[stream] presence ok, subject ${next ? 'received: ' + maskSubject(next) : 'NULL (registry outage, polling only)'}, heartbeat ${hbSeconds}s`);
        }
        if (subject && subscribedSubject !== subject) {
          if (await pageSubscribedAlerts()) {
            subscribedSubject = subject;
            log('[stream] the page already subscribes to this alerts subject itself, listening to it');
          } else {
            const ok = await postToWorker({ type: 'subscribe', instance: NATS_INSTANCE, channel: subject, coalesce: 'none' });
            if (ok) { subscribedSubject = subject; log('[stream] page did not subscribe on its own, subscribed on the page worker'); }
            else log('[stream] page worker not ready yet, will retry on the next beat');
          }
        }
      } else {
        presenceFailures++;
        log(`[stream] presence failed (status=${r.status ?? 'n/a'}${r.error ? ' ' + r.error : ''}) x${presenceFailures}`);
      }
    } catch (e) {
      presenceFailures++;
      log(`[stream] presence error: ${String(e).slice(0, 120)}`);
    }
    // The site beats every max(15, heartbeatIntervalSeconds) seconds. Retry sooner while not subscribed, but
    // when presence itself is failing (e.g. a Cloudflare challenge on this path) back off exponentially up
    // to 5 min instead of knocking every few seconds.
    const waitS = presenceFailures > 0
      ? Math.min(300, 10 * 2 ** Math.min(presenceFailures - 1, 5))
      : subject && subscribedSubject === subject ? Math.max(15, hbSeconds) : 10;
    if (!stopped) timer = setTimeout(() => { void beat(); }, waitS * 1000);
  }

  return {
    begin: () => { if (begun) return; begun = true; void beat(); },
    // "Healthy" = presence alive AND real events seen recently. A stream that connects but stops delivering
    // (it sat silent for hours before the subject-matching fix) must not be trusted: until the first event, and
    // after 2 quiet minutes, the sidecar goes back to polling at the site's own pace.
    healthy: () =>
      !!subject && subscribedSubject === subject && natsSocketSeen &&
      Date.now() - lastPresenceOkAt < 2.5 * Math.max(15, hbSeconds) * 1000 &&
      Date.now() - lastEventAt < 120_000,
    ready: () =>
      !!subject && subscribedSubject === subject && natsSocketSeen &&
      Date.now() - lastPresenceOkAt < 2.5 * Math.max(15, hbSeconds) * 1000 &&
      Date.now() - lastFrameAt < 30_000,
    subject: () => subject,
    describe: () =>
      `subject=${subject ? 'yes' : 'no'} subscribed=${subscribedSubject === subject && !!subject} nats=${natsSocketSeen} ` +
      `frames=${frames} events=${events} lastFrameAgo=${lastFrameAt ? Math.round((Date.now() - lastFrameAt) / 1000) + 's' : 'never'}`,
    stop: () => { stopped = true; clearInterval(wlogTimer); clearInterval(tallyTimer); if (timer) clearTimeout(timer); },
  };
}
