const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };

const BACKEND_URL = process.env.BACKEND_URL ?? 'http://127.0.0.1:8000';

export interface StructuredPumpNotificationRequest {
  externalId: string;
  kind: 'callout' | 'repost' | 'reply';
  actorHandle: string;
  actorUserId?: string | null;
  coinMint: string;
  chainId: number;
  symbol: string;
  marketCap?: number | null;
  originalAuthorHandle?: string | null;
  thesis?: string | null;
  positionCostBasisUsd?: number | null;
  replyContent?: string | null;
  createdAt: string;
}

export async function postPumpEvent(rawPayload: Record<string, unknown>): Promise<boolean> {
  try {
    const res = await fetch(`${BACKEND_URL}/api/pump-events`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(rawPayload),
    });
    const body = await res.json() as { accepted: boolean; reason?: string };
    return body.accepted;
  } catch (err) {
    console.error(`${ts()} [client] ❌ POST /api/pump-events failed:`, err);
    return false;
  }
}

export async function postStructuredPump(req: StructuredPumpNotificationRequest): Promise<void> {
  try {
    const res = await fetch(`${BACKEND_URL}/api/notifications/pump-structured`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(req),
    });
    const body = await res.json() as { accepted: boolean; reason?: string };
    if (body.accepted) {
      console.log(`${ts()} [client] ✅ accepted ${req.kind} externalId=${req.externalId} (${req.symbol} @${req.actorHandle})`);
    } else {
      console.log(`${ts()} [client] skipped externalId=${req.externalId}: ${body.reason}`);
    }
  } catch (err) {
    console.error(`${ts()} [client] ❌ POST /api/notifications/pump-structured failed for externalId=${req.externalId}:`, err);
  }
}

export async function heartbeat(): Promise<void> {
  try {
    await fetch(`${BACKEND_URL}/api/sidecar/heartbeat?source=pump-sidecar`, { method: 'POST' });
  } catch {
    console.warn(`${ts()} [client] ❌ heartbeat POST failed — backend unreachable?`);
  }
}
