import type { StructuredPumpNotificationRequest } from './client';

// Shapes trimmed to only what we read — pump.fun's real payloads carry far more.
interface PumpAuthor {
  userId: string;
  userName: string;
  walletAddress?: string | null;
}

interface PumpCalloutBody {
  calloutId: string;
  thesis: string;
}

interface PumpPosition {
  costBasisUsd?: number;
  amountBoughtUsd?: number;
}

// repost.callout / reply.callout: the ORIGINAL callout's author, nested one level
// around its own `callout` sub-object — see HANDOFF.md gotcha #3.
interface NestedOriginalCallout {
  userName: string;
  callout: PumpCalloutBody;
}

export interface PumpFeedItem {
  kind: string;
  author: PumpAuthor;
  coinMint: string;
  chainId: number;
  symbol: string;
  marketCap?: number | null;
  createdAt: string;
  callout?: PumpCalloutBody | null;
  position?: PumpPosition | null;
  repost?: {
    id: string;
    callout?: NestedOriginalCallout | null;
  } | null;
  reply?: {
    callout?: NestedOriginalCallout | null;
    reply?: { id: string; content: string } | null;
  } | null;
}

export interface TransformedPumpItem {
  externalId: string;
  structured: StructuredPumpNotificationRequest;
}

// Returns null for kinds we don't ingest (update/quote — see HANDOFF.md, txns deferred to Helius).
export function transformItem(item: PumpFeedItem): TransformedPumpItem | null {
  const base = {
    actorHandle: item.author.userName,
    actorUserId: item.author.userId,
    coinMint: item.coinMint,
    chainId: item.chainId,
    symbol: item.symbol,
    marketCap: item.marketCap ?? null,
    createdAt: item.createdAt,
  };

  if (item.kind === 'callout' && item.callout) {
    const externalId = item.callout.calloutId;
    return {
      externalId,
      structured: {
        ...base,
        externalId,
        kind: 'callout',
        originalAuthorHandle: item.author.userName,
        thesis: item.callout.thesis,
        positionCostBasisUsd: item.position?.costBasisUsd ?? item.position?.amountBoughtUsd ?? null,
      },
    };
  }

  if (item.kind === 'repost' && item.repost?.callout?.callout) {
    const externalId = item.repost.id;
    return {
      externalId,
      structured: {
        ...base,
        externalId,
        kind: 'repost',
        originalAuthorHandle: item.repost.callout.userName,
        thesis: item.repost.callout.callout.thesis,
      },
    };
  }

  if (item.kind === 'reply' && item.reply?.reply && item.reply?.callout?.callout) {
    const externalId = item.reply.reply.id;
    return {
      externalId,
      structured: {
        ...base,
        externalId,
        kind: 'reply',
        originalAuthorHandle: item.reply.callout.userName,
        replyContent: item.reply.reply.content,
      },
    };
  }

  return null;
}
