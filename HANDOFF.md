# Handoff: Pump.fun integration ("Groupchat Bot")

Written 2026-08-30 for whoever (human or Claude) picks this up next, most likely on the prod machine after pulling these changes. Read this before touching anything.

## Merge note (read this first if you're confused about migration IDs)

While this session's Platform work was in progress, `origin/master` had diverged — it had two commits this local branch didn't (from another Claude session working directly against prod, apparently), most notably `396cfd7 "add repeat window feature to limit buy/sell alerts per trader and contract"`. That's an unrelated feature: a per-user cooldown so the same trader+contract+side doesn't re-alert too often (`User.RepeatWindowMinutes`, a `(Trader, ContractAddress, Type)` index on `Notification`). A `git merge` was run to bring it in. Everything merged cleanly except `AppDbContextModelSnapshot.cs`, where both branches had touched the same generated region.

Resolution: took the incoming (repeat-window) snapshot as the base, then hand-re-added the Platform properties/index on top of it (rather than trying to text-merge two versions of EF-generated code). The original Platform migration (`20260830020059_...`) was generated against a now-stale snapshot, so it was deleted and **replaced by `20260830040000_AddPlatformToTraderAndNotification`** — same content, regenerated against the correct merged base. If you see references to the old `20260830020059` ID anywhere (old chat logs, etc.), it no longer exists — `20260830040000` is the real one.

Also worth knowing: `dotnet ef` design-time tooling (migrations add/list) got blocked mid-session by a Windows Application Control policy on this dev machine (`FileLoadException ... An Application Control policy has blocked this file`, loading the freshly-built DLL via reflection). Not a code issue — `dotnet build` works fine, only the EF reflection-based tooling was blocked. The final migration + its Designer.cs were hand-authored/spliced together and verified only via `dotnet build` (0 warnings), **not** verified with `dotnet ef database update` against this local db, since that hits the same block. Per explicit instruction, this wasn't chased further because prod's schema/history differs from this local dev copy anyway — whoever applies this for real should let EF generate/verify it fresh in an environment where the tooling actually runs, and treat the migration file here as a correct-by-construction starting point, not a proven-applied one.

## The objective

FomoFaster currently tracks traders on FOMO (fomo.family) only: a Playwright sidecar intercepts FOMO's WebSocket feed, POSTs structured buy/sell/thesis events to the .NET backend, which notifies Telegram subscribers.

Pump.fun shipped a near-identical social trading feature (follow traders, get notified on their buys/sells, see their "callouts" — a thesis-style curated post). The objective is to add Pump.fun as a **second source platform** feeding the same Telegram notification pipeline, without corrupting the existing FOMO data or behavior. Working name for this feature: **Groupchat Bot**.

Scope decision (user's call, locked in): **Solana-only for buy/sell txns, multi-chain for callouts.** Users will get per-trader toggles (buttons) for "get their txns" vs "get their callouts" — separate concerns, not bundled.

## What's confirmed, from reverse-engineering a real logged-in session (HAR captures + a live Playwright listener attached via CDP)

### The endpoint that matters most

```
GET https://frontend-api-v3.pump.fun/following-positions/alerts
    ?pageSize=20&kinds=callout,update,reply,quote,repost&minTradeAmountUsd=10&cursor=<cursor>
```

Cursor-paginated (`cursor` format: `<timestamp>:<uuid>`, taken from the last page). This is pump.fun's own "Friends" feed — same data their web UI renders. Requires an authenticated session (cookies); no separate API key.

Item shape (top level):
```jsonc
{
  "kind": "callout" | "update" | "reply" | "repost" | "quote", // quote never observed live, but is a valid supported kind
  "author": { "userId", "userName", "walletAddress", "isVerified", "xUsername", "profileImage" },
  "coinMint": "...",        // token contract address — Solana base58 OR 0x-prefixed EVM
  "chainId": 1399811149,    // pump.fun's chain id scheme — see mapping below
  "coinName", "symbol", "marketCap",
  "createdAt": "...",       // NOT a pure trade timestamp — see gotcha below
  "callout": { "calloutId", "thesis", "calledOutAtMcap", "multiple", "likes", "updates": [...], ... } | null,
  "position": { "amountHeld", "costBasisAmount", "costBasisUsd", "amountBought", "amountBoughtUsd", "realizedPnlUsd", "pnlUsd", "pnlPercentage", "valueUsd", "tokenPriceUsd" } | null,
  "reply": { ...nested callout + reply content... } | null,   // present for kind="update" and kind="reply"
  "repost": { ...nested original callout... } | null          // present for kind="repost"
}
```

### Chain ID mapping observed so far

- `1399811149` = Solana
- `8453` = Base
- `4663` = **ROBINHOOD** (inferred from a captured reply text referencing "RH" / "Ex IP/Story team... on RH" — same chain FomoFaster's `ChainInfo.cs` already tracks for FOMO)

Only 3 confirmed so far from a small sample. Expect BNB/ETH/MONAD to show up too given pump.fun's stated multi-chain terminal — extend the mapping as more chainIds are observed, don't assume this list is exhaustive.

### Kind types, real volume from ~90 captured items

| kind | count | meaning |
|---|---|---|
| `update` | 48 | Ambiguous — see gotcha below. Sometimes a real trade, sometimes a stale re-surface. |
| `callout` | 41 | New callout posted — `callout.thesis` is the text. This is the Thesis-equivalent. |
| `repost` | 10 | Trader amplifying **someone else's** callout. Real author/thesis is nested under `repost.callout`, not the reposter. |
| `reply` | 4 | Comment on an existing callout thread. |
| `quote` | 0 | Listed as a supported `kinds` value, never once observed firing. |

**Per the user's decision, this session is only wiring up `callout`, `repost`, `reply` for now** — `update` (txns) is deferred in favor of the Helius on-chain approach below.

### Gotchas — read these before writing ingestion code

1. **`author.walletAddress` is always a Solana address**, even when the trade/callout is on an EVM chain. Confirmed directly: user `ogantd`, same wallet `215nhcAHjQQGgwpQSJQ7zR26etbjjtVdW74NLzwEgQjP`, appeared on both a Solana trade (`CARDS`) and a `chainId:4663` (Robinhood) trade (`Invest`). This is their pump.fun account identity, not a per-chain wallet. **You cannot watch this address via an EVM chain explorer/webhook — it will never show EVM activity.**

2. **`kind:"update"` items are not reliably "a new trade happened."** Compare two snapshots of the same (author, coinMint) pair a few minutes apart — sometimes every `position` field is byte-identical (the item just got re-surfaced because someone else replied/reposted on the thread), sometimes the numbers genuinely changed (a real buy/sell). **You must diff `position` against the last-known snapshot per (author, coinMint) to tell the difference.** `createdAt` is an activity timestamp for the feed item, not proof of a fresh trade.

   Delta math that works, derived from real captured pairs:
   - Buy delta: `newAmountBoughtUsd - oldAmountBoughtUsd`
   - Sell proceeds: `(oldCostBasisUsd - newCostBasisUsd) + (newRealizedPnlUsd - oldRealizedPnlUsd)`

   (This is why txns were deferred to Helius instead — Helius gives atomic BUY/SELL events with no diffing required, at least for the Solana leg.)

3. **`repost`/`reply` need attribution unwrapping.** The real thesis/author for these lives nested (`repost.callout.callout.thesis`, `reply.callout.callout.thesis`), not at the top level. Get this wrong and you'll credit the wrong trader.

4. **Migration history was already out of sync before this session touched anything** — `WsEvents` and `ConfluenceAlerts` tables exist in the real DB but the last committed snapshot didn't reflect that. First attempt at the migration in this session tried to `CREATE TABLE` both from scratch and would have crashed with "table already exists" against a populated DB. Fixed for this migration by hand-editing it down to just the intended changes — but if you generate another migration and it tries to recreate tables that already exist, that's this same root cause resurfacing, not a new bug.

### Solana txns: separate plan, not yet implemented

Helius's Enhanced Transactions API has an explicit `PUMP_AMM` source that generates `BUY`/`SELL` transaction types directly (confirmed via Helius docs, not assumed) — no ambiguity, no diffing needed, unlike the feed's `update` items. Plan: register a Helius webhook watching the list of followed Pump traders' `walletAddress` values, filtered to `source: PUMP_AMM`. Helius API key already exists in this project's user-secrets (`Helius:ApiKey`) — no new credential needed. **EVM-chain txns have no equivalent plan yet** — no wallet to watch (see gotcha #1), so it'd have to fall back to the same position-diffing the feed itself requires. Not started.

## What's already implemented in this session (schema layer only — check git log/diff to confirm what actually landed)

- New `Platform` enum (`Fomo` | `Pump`) — `telegram-bot/TelegramBot/Models/Platform.cs`
- `Trader.Platform` added. **Unique index changed from `Handle` alone to `(Handle, Platform)`** — a FOMO trader and a Pump trader can now share a handle without colliding. This was a real bug waiting to happen, not speculative.
- `Notification.Platform` added.
- `NotificationType` extended with `Callout`, `Repost`, `Reply` — kept as their own distinct enum values, deliberately **not** aliased onto `Thesis`/`Buy`/`Sell`, per explicit instruction that pump typologies should stay distinguishable from FOMO's.
- `ITraderService`/`TraderService` — every handle-based method now takes an optional `Platform platform = Platform.Fomo` parameter. Existing FOMO call sites are unaffected (default preserves old behavior).
- `TraderService.BroadcastNewTraderMessageAsync` — was hardcoded "FOMO APP" branding + `x.com` profile link regardless of trader; now branches on `trader.Platform` for both the label and the profile link (`pump.fun/profile/{handle}` for Pump).
- EF Core migration `20260830040000_AddPlatformToTraderAndNotification` (this is the final ID — see the merge note at the top of this doc for why an earlier `20260830020059` version was superseded). Was applied and verified against the local dev DB (`telegram-bot/TelegramBot/fomofaster_ws.db`, a copy of the prod DB brought to this machine) **before** the merge — that applied-state is now stale relative to the current migration ID and the repeat-window migrations it doesn't have. Not fixed, per instruction not to chase local-db consistency further since prod's schema differs anyway.
  - Caught and fixed a second bug before applying: EF's auto-generated migration set `defaultValue: ""` (empty string) for the new `Platform` columns instead of `"Fomo"`, which would have made every existing row's `Platform` unparseable by the enum converter. Hand-corrected to `"Fomo"` before running it.
- Project builds clean, 0 warnings, migration applied without error.

## What's NOT done yet — the actual next steps

1. **Ingestion DTO** for Pump feed items — something like `StructuredPumpNotificationRequest`, shaped around pump's native fields (`kind`, `calloutId`, `author`, `coinMint`, `chainId`, `thesis`, etc.), analogous to how `StructuredNotificationRequest` works for FOMO but not forced into FOMO's field names.
2. **Raw audit/dedup table** for Pump events, analogous to `WsEvent` but pump-shaped (probably `PumpEvent`) — same purpose: avoid double-processing the same feed item across polls.
3. **A poller** (probably a new sidecar or an addition to `ws-sidecar`) that hits `following-positions/alerts` on an interval per the authenticated session, using the same CDP-attach-to-a-real-Chrome-profile pattern proven working in this session (see below), unwraps `repost`/`reply` attribution correctly, and POSTs structured events to the backend.
4. **Controller endpoint** to receive those POSTs, map `kind` → `NotificationType.Callout/Repost/Reply`, format the Telegram message, and route through the existing `_telegramService.SendNotificationToAllUsersAsync` pipeline — same shape as `NotificationsController.ReceiveStructuredNotification`, new endpoint, not a rewrite of the FOMO one.
5. **Per-trader follow preferences** (`UserTrader.WantTxns` / `WantCallouts` booleans, or similar) plus the Telegram inline-keyboard buttons to control them — not started, schema not yet touched for this specifically.
6. Helius `PUMP_AMM` webhook registration for Solana txns — not started.
7. EVM-chain txn strategy — not decided, no wallet address available for it (see gotcha #1).

## How the live data was captured, if you need to redo it

Chrome doesn't allow remote debugging on a default profile anymore (security restriction) — needed an isolated profile copy:
```
robocopy "%LOCALAPPDATA%\Google\Chrome\User Data\Default" "%LOCALAPPDATA%\ChromeDebugProfile\Default" /E /XD Cache "Code Cache" GPUCache "Service Worker" IndexedDB
copy "%LOCALAPPDATA%\Google\Chrome\User Data\Local State" "%LOCALAPPDATA%\ChromeDebugProfile\Local State"
"C:\Program Files\Google\Chrome\Application\chrome.exe" --remote-debugging-port=9222 --user-data-dir="%LOCALAPPDATA%\ChromeDebugProfile" --profile-directory="Default" "https://pump.fun"
```
Then connect via Playwright's `chromium.connectOverCDP('http://127.0.0.1:9222')`, find the pump.fun page, and `page.evaluate(fetch(...))` from inside the real page context so requests ride on the real session cookies. All the sample data referenced above came from this method, run against the user's own real pump.fun account (already following the traders in the samples) — none of it is synthetic.

The actual capture logs (`alerts-log.jsonl`, `alerts-log-full.jsonl`) live in a session-scoped temp scratchpad on the dev machine, not in this repo, so they won't be available wherever this handoff is being read. Everything load-bearing from them has been extracted into this document.
