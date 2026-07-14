import type { Page } from 'playwright';

const ts = () => { const d = new Date(); return `[${new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().replace('T', ' ').slice(0, 19)}]`; };
import { transformFrame, type StructuredNotificationRequest } from './transform';
import { insertEvent } from './db';

export interface WsTapHooks {
  /** A fomo.family WebSocket opened. */
  onTapOpen?: () => void;
  /** A fomo.family WebSocket closed. */
  onTapClose?: () => void;
}

export function attachWsInterceptor(
  page: Page,
  onTrade: (req: StructuredNotificationRequest) => Promise<void>,
  hooks: WsTapHooks = {}
): void {
  page.on('websocket', (ws) => {
    const isTap = ws.url().includes('fomo.family');
    console.log(`${ts()} [intercept] WebSocket opened: ${ws.url()}`);
    if (isTap) hooks.onTapOpen?.();

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

      insertEvent(payload);

      const req = transformFrame(payload);
      if (req) {
        onTrade(req).catch((err) =>
          console.error(`${ts()} [intercept] onTrade error:`, err)
        );
      }
    });

    ws.on('close', () => {
      console.log(`${ts()} [intercept] WebSocket closed: ${ws.url()}`);
      if (isTap) hooks.onTapClose?.();
    });
  });
}
