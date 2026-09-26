#!/usr/bin/env bash
# Step 1: snapshot the live DB (never query it in place) and export the trade tape, theses,
# Pump callouts and the tracked-trader list as JSON into ./data.
# Needs the sqlite3 CLI (https://sqlite.org/download.html) on PATH or in $SQLITE3.
set -euo pipefail
cd "$(dirname "$0")"
SQLITE3="${SQLITE3:-sqlite3}"
SRC="${DB:-../../telegram-bot/TelegramBot/fomofaster_ws.db}"
mkdir -p data/db
cp "$SRC" data/db/snapshot.db
[ -f "$SRC-wal" ] && cp "$SRC-wal" data/db/snapshot.db-wal || true
[ -f "$SRC-shm" ] && cp "$SRC-shm" data/db/snapshot.db-shm || true
Q() { "$SQLITE3" -json data/db/snapshot.db "$1"; }

# Raw JSON createdAt, not the CreatedAt column: older rows were stored with a local-time
# offset (see the audit report), the raw feed timestamp is always UTC.
Q "select UserHandle h, UserId uid, Type t, TokenAddress a, NetworkId n, Ticker k, coalesce(json_extract(RawJson,'\$.createdAt'),CreatedAt) c, cast(Price as real) p, cast(MarketCap as real) m, cast(UsdAmount as real) u, cast(Equity as real) e, json_extract(RawJson,'\$.isDev') dev from WsEvents where Type in ('swap_buy','swap_sell','transfer_out') order by 7" > data/fomo_trades.json
Q "select UserHandle h, TokenAddress a, NetworkId n, Ticker k, json_extract(RawJson,'\$.createdAt') c, json_extract(RawJson,'\$.comment.marketCapAtCreation') m, json_extract(RawJson,'\$.comment.priceUsdAtCreation') p, json_extract(RawJson,'\$.comment.comment') txt, json_extract(RawJson,'\$.authorTrade.realizedPnlUsd') rp, json_extract(RawJson,'\$.authorTrade.usdValue') uv from WsEvents where Type='thesis'" > data/fomo_thesis.json
Q "select ActorHandle h, ActorUserId uid, CoinMint a, ChainId n, Symbol k, json_extract(RawJson,'\$.createdAt') c, json_extract(RawJson,'\$.callout.calledOutAtMcap') m, json_extract(RawJson,'\$.callout.calloutPrice') p, json_extract(RawJson,'\$.callout.calloutId') id, json_extract(RawJson,'\$.author.walletAddress') w, json_extract(RawJson,'\$.author.xUsername') x, json_extract(RawJson,'\$.callout.quotedCalloutId') q from PumpEvents where Kind='callout' order by 6" > data/pump_callouts.json
Q "select Handle h, Platform pl, FirstSeenAt f from Traders" > data/traders.json
Q "select 'fomo' pl, UserHandle h, json_extract(RawJson,'$.profilePictureLink') img, max(Id) id from WsEvents where json_extract(RawJson,'$.profilePictureLink') is not null group by UserHandle union all select 'pump', ActorHandle, json_extract(RawJson,'$.author.profileImage'), max(Id) from PumpEvents where json_extract(RawJson,'$.author.profileImage') is not null group by ActorHandle" > data/avatars.json
Q "select TokenAddress a, NetworkId n, Ticker k, WindowStartedAt ws, FiredAt f, TraderCount tc, cast(TotalUsd as real) usd from ConfluenceAlerts order by FiredAt" > data/confluence.json
ls -la data/*.json
