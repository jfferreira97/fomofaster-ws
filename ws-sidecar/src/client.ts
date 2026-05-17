import type { StructuredNotificationRequest } from './transform';

const ts = () => `[${new Date().toISOString().replace('T', ' ').slice(0, 19)}]`;

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
      console.log(`${ts()} [client] ✅ accepted tradeId=${req.tradeId} (${req.side} ${req.ticker} @${req.trader})`);
    } else {
      console.log(`${ts()} [client] skipped tradeId=${req.tradeId}: ${body.reason}`);
    }
  } catch (err) {
    console.error(`${ts()} [client] ❌ POST failed for tradeId=${req.tradeId}:`, err);
  }
}

export async function heartbeat(): Promise<void> {
  const url = `${BACKEND_URL}/api/sidecar/heartbeat`;
  try {
    await fetch(url, { method: 'POST' });
  } catch {
    console.warn(`${ts()} [client] ❌ heartbeat POST failed — backend unreachable?`);
  }
}
