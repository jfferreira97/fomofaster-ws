import type { StructuredNotificationRequest } from './transform';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const BACKEND_URL = process.env.BACKEND_URL ?? 'http://127.0.0.1:8000';

export async function postStructured(req: StructuredNotificationRequest): Promise<void> {
  const url = `${BACKEND_URL}/api/notifications/structured`;
  try {
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    });
    const body = await res.json() as { accepted: boolean; reason?: string };
    if (body.accepted) {
      console.log(`${ts()} [client] ✅ accepted wsId=${req.wsId} (${req.wsType} ${req.ticker} @${req.trader})`);
    } else {
      console.log(`${ts()} [client] skipped wsId=${req.wsId}: ${body.reason}`);
    }
  } catch (err) {
    console.error(`${ts()} [client] ❌ POST failed for tradeId=${req.tradeId}:`, err);
  }
}

// What the sidecar says about itself with each heartbeat, for the backend's watchdog. A sidecar whose page lost its login keeps
// sending heartbeats all the same: on 2026-11-18 that went unseen for 10 hours, so the heartbeat now says whether the page is live.
export interface SidecarStatus {
  state: 'ok' | 'no_connection';
  since?: string | null;       // when the current problem began (ISO), null while all is well
  detail?: string | null;
  // The site's cookies that have an expiry: name, domain and expiry only, never the value. The backend finds the login cookie
  // among them and warns a few days before it runs out.
  cookies?: { name: string; domain: string; expires: number }[];
}

export async function heartbeat(status?: SidecarStatus): Promise<void> {
  const url = `${BACKEND_URL}/api/sidecar/heartbeat`;
  try {
    await fetch(url, {
      method: 'POST',
      ...(status ? { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(status) } : {}),
    });
  } catch {
    console.warn(`${ts()} [client] ❌ heartbeat POST failed — backend unreachable?`);
  }
}
