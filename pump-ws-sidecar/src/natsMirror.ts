import type { Page } from 'playwright';
import type { StreamEvent } from './stream';

// A second pair of ears on the alerts stream, from Node itself. pump.fun's CORE stream is served by several servers, and a
// connection can land on one that stays open but delivers nothing for our subject (measured 2026-10-29: 1 connection in 6 got
// nothing, 1 got about 60%, the others everything). The page's own worker has a single connection, so when it lands on such a
// server the alerts only arrive through the slow safety poll (the "Pump live stream Down" of 2026-10-28). This opens a few more
// connections to the same stream, subscribed to the same subject the presence call returned, and hands every event to the same
// path as the page's (index.ts keeps only the first copy of each event, whichever source brought it).
// It connects exactly as pump.fun's worker does, with what the page hands its worker (captured by the init script in index.ts):
//   - the CORE server and the site's public subscriber login from the worker's 'configs' message
//   - the per-user token from its 'auth' message, once pump.fun turns that on (CONNECT {auth_token}, no user/pass), so the
//     day the public login loses access to our subject this keeps working with no change here.
// Presence (what makes pump.fun publish our alerts at all) stays with the page: this only listens.
// A connection that hears nothing for a while when the page or another connection is hearing events is closed and opened
// again, which normally lands it on another server.

interface MinimalWebSocket {
  binaryType: string;
  onopen: (() => void) | null;
  onmessage: ((ev: { data: unknown }) => void) | null;
  onclose: (() => void) | null;
  onerror: (() => void) | null;
  send(data: string): void;
  close(): void;
}
// Node 22+ has WebSocket built in; this project's Node types predate it.
const WS = (globalThis as unknown as { WebSocket?: new (url: string) => MinimalWebSocket }).WebSocket;

interface CoreSettings { servers?: string; user?: string; pass?: string }
interface PageNats { core: CoreSettings | null; jwt: string | null }

const SILENT_RECYCLE_MS = 2 * 60_000;   // nothing heard here for this long while others hear events: try another server
const OTHERS_HEARD_MS = 120_000;
const PING_MS = 20_000;                 // our own pings: a socket that stops answering 2 of them is dead
const CHECK_MS = 5_000;

export interface NatsMirror {
  describe(): string;
  stop(): void;
}

export function startNatsMirror(opts: {
  page: () => Page;                        // the current page (a rotation swaps it)
  subject: () => string | null;            // the alerts subject pattern from the presence call
  pageHearing: () => boolean;              // the page's own stream delivered events recently
  onEvent: (ev: StreamEvent) => void;
  log: (msg: string) => void;
  connections: number;
}): NatsMirror {
  const { log } = opts;
  let stopped = false;
  const errorsLogged = new Set<string>();

  async function readPage(): Promise<PageNats> {
    const s = await opts.page().evaluate('JSON.stringify({ core: window.__natsCore || null, jwt: window.__natsJwt || null })').catch(() => null);
    try { return JSON.parse(String(s)) as PageNats; } catch { return { core: null, jwt: null }; }
  }

  class Conn {
    ws: MinimalWebSocket | null = null;
    server = '';
    connected = false;
    connectedAt = 0;
    lastEventAt = 0;
    events = 0;
    subscribed: string | null = null;
    sid = 0;
    pingsOut = 0;
    infoSeen = false;
    attempt = 0;
    retryTimer: NodeJS.Timeout | null = null;
    buf: Uint8Array = new Uint8Array(0);

    constructor(readonly n: number) {}

    async open(): Promise<void> {
      if (stopped || this.ws) return;
      const nats = await readPage();
      const url = nats.core?.servers?.split(',')[0]?.trim();
      if (!WS || !url || (!nats.jwt && nats.core?.user === undefined)) { this.retry(15_000); return; }   // the page has not started its worker yet
      let ws: MinimalWebSocket;
      try { ws = new WS(`wss://${url.replace(/^(?:wss?|nats|tls):\/\//, '')}`); } catch { this.retry(); return; }
      this.ws = ws;
      this.buf = new Uint8Array(0);
      this.infoSeen = false;
      ws.binaryType = 'arraybuffer';
      ws.onmessage = (ev) => this.data(ev.data, nats);
      ws.onclose = () => this.closed();
      ws.onerror = () => { /* onclose follows */ };
    }

    send(s: string): void { try { this.ws?.send(s); } catch { /* closing */ } }

    data(d: unknown, nats: PageNats): void {
      const chunk = typeof d === 'string' ? new TextEncoder().encode(d) : new Uint8Array(d as ArrayBuffer);
      const b = new Uint8Array(this.buf.length + chunk.length);
      b.set(this.buf); b.set(chunk, this.buf.length);
      this.buf = b;
      // NATS text protocol: "<op> ...\r\n", and MSG/HMSG carry a payload of exactly <bytes> after the header line.
      for (;;) {
        const nl = indexOfCrlf(this.buf);
        if (nl < 0) return;
        const line = new TextDecoder().decode(this.buf.subarray(0, nl));
        const m = /^(H?MSG) (\S+) (\S+)(?: (\S+))? (\d+)(?: (\d+))?$/.exec(line);
        if (m) {
          const hdr = m[1] === 'HMSG' ? +m[5] : 0;
          const total = m[1] === 'HMSG' ? +m[6] : +m[5];
          if (this.buf.length < nl + 2 + total + 2) return;            // the rest of the payload is in a later frame
          const payload = new TextDecoder().decode(this.buf.subarray(nl + 2 + hdr, nl + 2 + total));
          this.buf = this.buf.subarray(nl + 2 + total + 2);
          this.message(payload);
          continue;
        }
        this.buf = this.buf.subarray(nl + 2);
        if (line.startsWith('INFO ')) {
          // The server sends INFO again later (cluster updates). Only the first one gets a CONNECT, as pump.fun's worker does:
          // a second CONNECT on a live connection silently drops its subscriptions.
          if (this.infoSeen) continue;
          this.infoSeen = true;
          try { this.server = String((JSON.parse(line.slice(5)) as { server_name?: string }).server_name ?? '').slice(0, 14); } catch { /* keep going */ }
          const base = { verbose: false, pedantic: false, tls_required: false, name: '', lang: 'pumpfun-worker'/*'javascript'*/, version: '1', protocol: 1, headers: true, no_responders: true };
          this.send(`CONNECT ${JSON.stringify(nats.jwt ? { ...base, auth_token: nats.jwt } : { ...base, user: nats.core?.user, pass: nats.core?.pass })}\r\nPING\r\n`);
        } else if (line === 'PONG') {
          this.pingsOut = 0;
          if (!this.connected) { this.connected = true; this.connectedAt = Date.now(); this.subscribed = null; this.sync(); }
        } else if (line === 'PING') this.send('PONG\r\n');
        else if (line.startsWith('-ERR')) {
          const why = line.replace(/alertsFeed\.user\.[^' ]+/g, 'alertsFeed.user.<uid>').slice(0, 120);
          if (!errorsLogged.has(why) && errorsLogged.size < 20) { errorsLogged.add(why); log(`[mirror] server error: ${why}`); }
          if (/authorization|authentication|permissions/i.test(line)) { this.attempt = Math.max(this.attempt, 5); this.ws?.close(); }
        }
      }
    }

    message(payload: string): void {
      let j: unknown;
      try { j = JSON.parse(payload); if (typeof j === 'string') j = JSON.parse(j); } catch { return; }
      const e = j as { kind?: unknown; id?: unknown; createdAt?: unknown };
      if (!e || typeof e.kind !== 'string' || typeof e.id !== 'string') return;
      this.events++;
      this.lastEventAt = Date.now();
      this.attempt = 0;
      opts.onEvent({ kind: e.kind, id: e.id, createdAt: typeof e.createdAt === 'string' ? e.createdAt : undefined, receivedAtMs: Date.now(), raw: j });
    }

    // Subscribe to the subject the presence call gave the page (and follow it if it changes).
    sync(): void {
      if (!this.connected) return;
      const want = opts.subject();
      if (want === this.subscribed) return;
      if (this.subscribed) this.send(`UNSUB ${this.sid}\r\n`);
      this.subscribed = want;
      if (want) { this.sid++; this.send(`SUB ${want} ${this.sid}\r\n`); }
    }

    tick(othersHearing: boolean): void {
      if (!this.ws || !this.connected) return;
      this.sync();
      const quietFor = Date.now() - Math.max(this.lastEventAt, this.connectedAt);
      if (othersHearing && this.subscribed && quietFor > SILENT_RECYCLE_MS) {
        log(`[mirror] connection ${this.n} on ${this.server || '?'} heard nothing for ${Math.round(quietFor / 60_000)} min while events were arriving elsewhere, reconnecting`);
        this.ws.close();
      }
    }

    ping(): void {
      if (!this.ws || !this.connected) return;
      if (this.pingsOut >= 2) { this.ws.close(); return; }
      this.pingsOut++;
      this.send('PING\r\n');
    }

    closed(): void {
      this.ws = null; this.connected = false; this.subscribed = null; this.pingsOut = 0;
      this.retry();
    }

    retry(fixedMs?: number): void {
      if (stopped || this.retryTimer) return;
      const ms = fixedMs ?? Math.min(300_000, 2_000 * 2 ** Math.min(this.attempt, 8));
      this.attempt++;
      this.retryTimer = setTimeout(() => { this.retryTimer = null; void this.open(); }, ms);
    }

    stop(): void { if (this.retryTimer) clearTimeout(this.retryTimer); this.ws?.close(); }
  }

  const conns = Array.from({ length: Math.max(1, opts.connections) }, (_, i) => new Conn(i + 1));
  conns.forEach((c, i) => c.retry(3_000 + i * 2_000));   // the page starts its worker first

  const checkTimer = setInterval(() => {
    for (const c of conns) {
      const othersHearing = opts.pageHearing() || conns.some((o) => o !== c && Date.now() - o.lastEventAt < OTHERS_HEARD_MS);
      c.tick(othersHearing);
    }
  }, CHECK_MS);
  const pingTimer = setInterval(() => conns.forEach((c) => c.ping()), PING_MS);
  if (!WS) log('[mirror] this Node has no built-in WebSocket (needs Node 22+), mirror off');

  return {
    describe: () => `mirror ${conns.filter((c) => c.connected && c.subscribed).length}/${conns.length} events=${conns.map((c) => c.events).join('+')}`,
    stop: () => { stopped = true; clearInterval(checkTimer); clearInterval(pingTimer); conns.forEach((c) => c.stop()); },
  };
}

function indexOfCrlf(b: Uint8Array): number {
  for (let i = 0; i + 1 < b.length; i++) if (b[i] === 13 && b[i + 1] === 10) return i;
  return -1;
}
