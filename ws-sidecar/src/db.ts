import type { FomoWebsocketPayload } from './transform';

const BACKEND_URL = process.env.BACKEND_URL ?? 'http://127.0.0.1:8000';

export function insertEvent(payload: FomoWebsocketPayload): void {
  fetch(`${BACKEND_URL}/api/ws-events`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  }).catch(() => {});
}
