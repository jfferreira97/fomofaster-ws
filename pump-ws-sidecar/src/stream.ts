import type { Page } from 'playwright';

// pump.fun's own live path for the "following" alerts feed, copied from its frontend:
//   1. POST /following-positions/alerts/presence registers this viewer and returns the exact NATS
//      subject to consume plus the heartbeat cadence ("call this every heartbeatIntervalSeconds").
//      The server publishes stream events ONLY for viewers with live presence.
//   2. The page's own NATS Web Worker is told to subscribe to that subject on the CORE instance
//      ({type:'subscribe', instance:'CORE', channel:<subject>}), exactly as useNatsSubscriptionHandler does.
//   3. Events arrive as NATS frames on the page's nats.realtime.pump.fun websocket. For callouts, replies
//      and reposts the event is only a doorbell ("a new row exists"): the site then re-fetches the feed.
//      Trades are inserted straight from the event; we do not ingest trades, so they are ignored upstream.
// We do not read or reuse the stream's login: the logged-in page sets that up for its own worker, and
// we only listen to what the page itself receives (same approach as bag-watch's pump trades tab).
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
  let natsSocketSeen = false;
  let frames = 0;
  let events = 0;
  let presenceFailures = 0;
  let stopped = false;
  let begun = false;
  let timer: NodeJS.Timeout | undefined;

  page.on('websocket', (ws) => {
    if (!NATS_WS.test(ws.url())) return;
    natsSocketSeen = true;
    log(`[stream] nats websocket open`);
    // a new socket means the worker reconnected: subscriptions live in the worker, so re-send ours
    subscribedSubject = null;
    if (begun) { if (timer) clearTimeout(timer); timer = setTimeout(() => { void beat(); }, 3_000); }
    ws.on('framereceived', (f) => onFrame(String(f.payload)));
    ws.on('close', () => { natsSocketSeen = false; log('[stream] nats websocket closed'); });
  });

  function onFrame(s: string): void {
    lastFrameAt = Date.now();
    frames++;
    if (!subject || !s.includes(subject)) return;
    // NATS text protocol: "MSG <subject> <sid> [reply] <len>\r\n<payload>\r\n", several may share a frame.
    const lines = s.split(/\r?\n/);
    for (let i = 0; i < lines.length - 1; i++) {
      const m = /^MSG (\S+) \S+(?: \S+)? \d+$/.exec(lines[i]);
      if (!m || m[1] !== subject) continue;
      let j: unknown;
      try { j = JSON.parse(lines[i + 1]); if (typeof j === 'string') j = JSON.parse(j); } catch { continue; }
      i++;
      const e = j as { kind?: unknown; id?: unknown; createdAt?: unknown };
      if (!e || typeof e.kind !== 'string' || typeof e.id !== 'string') continue;
      events++;
      onEvent({ kind: e.kind, id: e.id, createdAt: typeof e.createdAt === 'string' ? e.createdAt : undefined, receivedAtMs: Date.now(), raw: j });
    }
  }

  // browser-side code as a string: this project's tsconfig has no DOM lib
  const postToWorker = (msg: object): Promise<boolean> =>
    page.evaluate(
      `(() => { const w = window.__workers && window.__workers[0]; if (!w) return false; w.postMessage(${JSON.stringify(msg)}); return true; })()`,
    ).then(Boolean).catch(() => false);

  async function registerPresence(): Promise<PresenceResult> {
    return page.evaluate(async ({ url, body }) => {
      try {
        const res = await fetch(url, { method: 'POST', credentials: 'include', headers: { 'content-type': 'application/json' }, body });
        if (!res.ok) return { ok: false, status: res.status };
        return { ok: true, data: await res.json() };
      } catch (e) {
        return { ok: false, error: String(e) };
      }
    }, { url: PRESENCE_URL, body: JSON.stringify({ filters: PRESENCE_FILTERS }) }) as Promise<PresenceResult>;
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
          log(`[stream] presence ok, subject ${next ? 'received' : 'NULL (registry outage, polling only)'}, heartbeat ${hbSeconds}s`);
        }
        if (subject && subscribedSubject !== subject) {
          const ok = await postToWorker({ type: 'subscribe', instance: NATS_INSTANCE, channel: subject, coalesce: 'none' });
          if (ok) { subscribedSubject = subject; log('[stream] subscribed on the page worker'); }
          else log('[stream] page worker not ready yet, will retry on the next beat');
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
    healthy: () =>
      !!subject && subscribedSubject === subject && natsSocketSeen &&
      Date.now() - lastPresenceOkAt < 2.5 * Math.max(15, hbSeconds) * 1000,
    describe: () =>
      `subject=${subject ? 'yes' : 'no'} subscribed=${subscribedSubject === subject && !!subject} nats=${natsSocketSeen} ` +
      `frames=${frames} events=${events} lastFrameAgo=${lastFrameAt ? Math.round((Date.now() - lastFrameAt) / 1000) + 's' : 'never'}`,
    stop: () => { stopped = true; if (timer) clearTimeout(timer); },
  };
}
