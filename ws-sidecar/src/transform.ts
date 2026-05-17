export interface StructuredNotificationRequest {
  tradeId: string;
  trader: string;
  ticker: string;
  contractAddress: string;
  networkId: number;
  side: string;
  usdAmount: number;
  marketCap?: number;
  comment?: string;
  createdAt: string;
}

// In-memory LRU to skip redundant POSTs after WS reconnect replays
const LRU_MAX = 500;
const seenTradeIds: string[] = [];

function markSeen(tradeId: string): boolean {
  if (seenTradeIds.includes(tradeId)) return true;
  seenTradeIds.push(tradeId);
  if (seenTradeIds.length > LRU_MAX) seenTradeIds.shift();
  return false;
}

export function transformFrame(payload: Record<string, unknown>): StructuredNotificationRequest | null {
  const type = payload.type as string;
  const tradeId = (payload.tradeId ?? payload.id) as string;

  if (markSeen(tradeId)) {
    console.log(`[transform] duplicate tradeId ${tradeId}, skipping`);
    return null;
  }

  const trader = payload.userHandle as string;
  const ticker = payload.ticker as string;
  const contractAddress = payload.tokenAddress as string;
  const networkId = payload.networkId as number;
  const createdAt = payload.createdAt as string;

  if (type === 'swap_buy' || type === 'swap_sell' || type === 'swap_withdraw') {
    return {
      tradeId,
      trader,
      ticker,
      contractAddress,
      networkId,
      side: type,
      usdAmount: payload.usdAmount as number,
      marketCap: payload.marketCap as number | undefined,
      createdAt,
    };
  }

  if (type === 'thesis') {
    const authorTrade = payload.authorTrade as Record<string, unknown>;
    const commentObj = payload.comment as Record<string, unknown> | undefined;
    // closedAt === null → still holding → buy; non-null → position closed → sell
    const thesisSide = authorTrade.closedAt == null ? 'swap_buy' : 'swap_sell';

    return {
      tradeId,
      trader,
      ticker,
      contractAddress,
      networkId,
      side: thesisSide,
      usdAmount: authorTrade.usdValue as number,
      createdAt,
      comment: commentObj?.comment as string | undefined,
    };
  }

  console.log(`[transform] unknown payload type "${type}", skipping`);
  return null;
}
