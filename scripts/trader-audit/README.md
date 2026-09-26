# Trader audit

Rates every tracked trader on what a **follower** got by acting on their alerts, then writes
the per-trader public-profile stats the `/manage` page shows (`telegram-bot/TelegramBot/Intel/trader-intel.json`,
hot-reloaded, no restart needed) plus a standalone HTML report.

This is the research version. Day to day, the bot categorizes traders itself:
`TraderCategorizerService` replays new posts daily and applies the same rules (ported in
`FollowerReplay.cs` and `TraderRatingCalculator.cs`), and admins can move traders by hand from the
dashboard's Categories tab. The app reads only the profile fields from `trader-intel.json` (PnL,
followers, links, avatar), plus the categories once, to seed a fresh database.

Plain Node 20, no dependencies. Everything lands in `./data` (gitignored). The window is the
30 days ending at `AUDIT_END` (default: today, UTC); see `window.js`.

## Run

```bash
./export.sh                          # 1. snapshot the DB, export tape/callouts to JSON
node build_calls.js                  # 2. calls: FOMO first buys (+ trader round trips), Pump callouts
node fetch_candles.js --conc 20      # 3. 1m candles around every call (pump.fun candle service, resumable, ~3-4h)
node fetch_candles.js --conc 12      #    ...re-run once to retry the windows that 503'd
node scrape_pump.js                  # 4. public Pump profiles: callout history, leaderboard stats, portfolio + per-wallet PnL
node scrape_pump_all.js              #    ...and every tracked Pump trader (followers, X, pump callout stats, 30d/7d PnL)
node match_wallets.js --per 4        # 5. FOMO trader -> on-chain wallet (matches our fills to pump.fun's trade tape)
node wallet_pnl.js                   #    ...and their public 1d/7d/30d PnL
node analyze.js                      # 6. replay each call as a follower (entry on the next 1m candle, 3 exit styles, 3% costs)
node profiles.js                     # 7. full-history profiles (own round trips, Pump public stats, wallet PnL)
node persistence.js                  # 8. which traits are stable / predictive between window halves
node select_score.js                 # 9. out-of-sample test of scoring rules (writes score_validation.json)
node build_intel.js                  # 10. styles, cut list (hard/soft), alert levels, categories -> data/trader-intel.json
node research_alerts.js              # 11. which alert types / settings / latencies are actionable (needs data/confluence.json, see below)
node build_report.js                 # 12. report/trader-audit.html
cp data/trader-intel.json ../../telegram-bot/TelegramBot/Intel/trader-intel.json
```

## What the numbers mean

- **Avg / call**: follower return after 3% fees, averaged over three exit styles (scalp +50%/-30% 1h,
  flip 2x/-30% 4h, runner 3x/-40% 24h), shrunk toward the population average (k=30).
- **Score**: z(2x rate) + z(median 1h return) - z(-30% stop-out rate) + z(Avg / call). Chosen because
  it held up out of sample in both directions; past EV alone does not (see `select_score.js`).
- **Cut**: bottom 30% by score with negative Avg / call, or Avg / call below -6%, or 20+ alerts/day
  with no edge. **Hard** = bottom 20% by score, below -8%, or high volume with negative returns.
- A trader's own win rate does not predict follower returns (rank corr ~0); it is shown, not ranked on.

## Data hygiene

The candle service occasionally serves impossible EVM prints (1e28x wicks, unit switches mid-series).
`lib.cleanBars` clamps wicks to 6x a rolling median and ends a series at the first 30x jump; both
`analyze.js` and the research scripts use it. Without it, hit rates and hold-to-close returns are inflated.
