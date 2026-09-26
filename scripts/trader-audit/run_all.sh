#!/usr/bin/env bash
# Monthly refresh: re-rates every trader on the last 30 days and drops the new ratings into
# the app (hot-reloaded; everyone on an alert level is re-picked within ~5 minutes).
# Network steps are resumable, so re-running after a failure picks up where it stopped.
# Takes a few hours, mostly candle fetching. Needs Node 20+ and the sqlite3 CLI.
set -euo pipefail
cd "$(dirname "$0")"
export AUDIT_END="${AUDIT_END:-$(date -u +%Y-%m-%d)}"
N="node --max-old-space-size=12288"
step() { echo; echo "== $(date -u +%H:%M:%S) $*"; }

step "export";          ./export.sh
step "calls";           $N build_calls.js
rm -rf data/candles data/pump_profiles data/pump_users data/fomo_wallets.json data/fomo_wallet_pnl.json data/pump_pnl_*.json
step "candles";         $N fetch_candles.js --conc 20
step "candles retry";   $N fetch_candles.js --conc 12 || true
step "pump profiles";   $N scrape_pump.js
step "pump users";      $N scrape_pump_all.js
step "fomo wallets";    $N match_wallets.js --per 6 && $N match_wallets.js --per 12 --retry-all || true
step "wallet pnl";      $N wallet_pnl.js || true
step "replay";          $N analyze.js
step "profiles";        $N profiles.js
step "persistence";     $N persistence.js > data/persistence.log
step "score check";     $N select_score.js
step "ratings";         $N build_intel.js
step "alert research";  $N research_alerts.js > data/research_alerts.log || true
step "thesis vs callout"; $N research_platform.js > data/research_platform.log || true
step "report";          $N build_report.js

cp data/trader-intel.json ../../telegram-bot/TelegramBot/Intel/trader-intel.json
echo; echo "Done: ratings for window ending $AUDIT_END copied into the app; report at report/trader-audit.html"
