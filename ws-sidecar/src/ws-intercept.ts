import fs from 'fs';
import type { Page } from 'playwright';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };
import { transformFrame, type StructuredNotificationRequest } from './transform';
import { insertEvent } from './db';

const LOG_FILE = 'C:\\Users\\Administrator\\Desktop\\ws-payloads.txt';

function logPayload(payload: unknown): void {
  try {
    const line = `[${new Date().toISOString()}] ${JSON.stringify(payload, null, 2)}\n${'─'.repeat(80)}\n`;
    fs.appendFileSync(LOG_FILE, line, 'utf8');
  } catch {
    // never crash the sidecar over a logging failure
  }
}

export function attachWsInterceptor(
  page: Page,
  onTrade: (req: StructuredNotificationRequest) => Promise<void>
): void {
  page.on('websocket', (ws) => {
    console.log(`${ts()} [intercept] WebSocket opened: ${ws.url()}`);

    ws.on('framereceived', (frame) => {
      const raw = typeof frame.payload === 'string' ? frame.payload : null;
      if (!raw) return;
      let msg: Record<string, unknown>;
      try {
        msg = JSON.parse(raw) as Record<string, unknown>;
      } catch {
        return; // binary or non-JSON frame
      }

      // Only care about trading_activity data events
      if (msg.type !== 'data' || msg.topicType !== 'trading_activity') return;

      const payload = msg.payload as Record<string, unknown> | undefined;
      if (!payload) return;

      // Log raw payload to desktop for inspection
      logPayload(payload);
      insertEvent(payload);

      const req = transformFrame(payload);
      if (req) {
        onTrade(req).catch((err) =>
          console.error(`${ts()} [intercept] onTrade error:`, err)
        );
      }
    });

    ws.on('close', () => console.log(`${ts()} [intercept] WebSocket closed`));
  });
}
