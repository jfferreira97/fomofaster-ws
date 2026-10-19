import { appendFile, mkdirSync } from 'fs';
import { join } from 'path';

// Followed traders' pump.fun trades as they arrive on the live stream (stream.ts), one JSON object per line,
// one file per UTC day in ./logs. Record-only: nothing is sent to the backend or to users — the backend has
// no pump-trade notification yet; this is the raw material for designing it. Public trade data only.
const LOG_DIR = join(__dirname, '..', 'logs');
mkdirSync(LOG_DIR, { recursive: true });

export interface PumpTradeRecord {
  seenAt: string;          // when we received the stream event
  createdAt: string | null; // when pump.fun says the trade happened
  lagMs: number | null;    // seenAt - createdAt
  eventId: string;
  trader: string | null;
  traderUserId: string | null;
  traderVerified: boolean | null;
  wallet: string | null;
  mint: string | null;
  chainId: number | null;
  symbol: string | null;
  isBuy: boolean | null;
  amountUsd: number | null;
  priceUsd: number | null;
  baseAmount: number | null;
  tx: string | null;
}

export function logPumpTrade(rec: PumpTradeRecord): void {
  appendFile(join(LOG_DIR, `trades-${rec.seenAt.slice(0, 10)}.jsonl`), JSON.stringify(rec) + '\n', () => { /* best effort */ });
}

type Obj = Record<string, unknown>;
const str = (v: unknown): string | null => (typeof v === 'string' ? v : null);
const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const bool = (v: unknown): boolean | null => (typeof v === 'boolean' ? v : null);

/** Shape per pump.fun's own handler: { kind:'trade', id, createdAt, author:{userId,userName,walletAddress,isVerified}, coin:{mint,chainId,symbol}, trade:{tx,isBuy,amountUsd,priceUsd,baseAmount} }. Stub events carry no trade. */
export function tradeFromEvent(raw: unknown, receivedAtMs: number): PumpTradeRecord | null {
  const e = raw as Obj | null;
  if (!e || e.kind !== 'trade' || e.stub) return null;
  const trade = e.trade as Obj | undefined, author = e.author as Obj | undefined, coin = e.coin as Obj | undefined;
  if (!trade || !author || !coin) return null;
  const createdAt = str(e.createdAt);
  const created = createdAt ? Date.parse(createdAt) : NaN;
  return {
    seenAt: new Date(receivedAtMs).toISOString(),
    createdAt,
    lagMs: Number.isFinite(created) ? receivedAtMs - created : null,
    eventId: String(e.id),
    trader: str(author.userName), traderUserId: str(author.userId), traderVerified: bool(author.isVerified), wallet: str(author.walletAddress),
    mint: str(coin.mint), chainId: num(coin.chainId), symbol: str(coin.symbol),
    isBuy: bool(trade.isBuy), amountUsd: num(trade.amountUsd), priceUsd: num(trade.priceUsd), baseAmount: num(trade.baseAmount), tx: str(trade.tx),
  };
}
