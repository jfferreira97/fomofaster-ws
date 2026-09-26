namespace TelegramBot.Models;

// One trader's first post on one token (a FOMO thesis, a FOMO buy, or a pump.fun callout),
// replayed as a follower who acted on the alert: entry at the open of the first 1-minute candle
// after the post, then what the price did over the next 24h. Written once the 24h have passed
// and never recomputed, so TraderCategorizerService only fetches candles for new calls.
public class CallOutcome
{
    public int Id { get; set; }
    public int TraderId { get; set; }
    public string Kind { get; set; } = string.Empty;     // thesis | buy | callout
    public int NetworkId { get; set; }
    public string TokenAddress { get; set; } = string.Empty;
    public string? Ticker { get; set; }
    public DateTime CalledAt { get; set; }
    public double? CallPrice { get; set; }
    public double? McapAtCall { get; set; }

    // Buys only: share of the position the trader sold within 10 minutes (dumping on followers).
    public double? SoldIn10mFrac { get; set; }

    public string Status { get; set; } = CallOutcomeStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    public string? Source { get; set; }                  // candles | tape

    // Follower result, relative to the entry price (1.0 = entry). Null unless Status = ok.
    public double? EntryVsCall { get; set; }             // how far price had moved by the time a follower got in
    public double? R1h { get; set; }                     // return 1h after entry
    public double? R24h { get; set; }
    public double? Pk15m { get; set; }                   // highest high within 15m, as a multiple of entry
    public double? Pk1h { get; set; }
    public double? Pk24h { get; set; }
    public double? MinutesToPeak { get; set; }
    public bool? Sl30 { get; set; }                      // tagged -30% within 4h
    public double? EvQuick { get; set; }                 // +30% / -20% / out by 15m, after costs
    public double? EvScalp { get; set; }                 // +50% / -30% / out by 1h, after costs
    public double? EvFlip { get; set; }                  // 2x / -30% / out by 4h
    public double? EvRunner { get; set; }                // 3x / -40% / out by 24h
}

public static class CallOutcomeStatus
{
    public const string Pending = "pending";
    public const string Ok = "ok";
    public const string NoData = "nodata";
}
