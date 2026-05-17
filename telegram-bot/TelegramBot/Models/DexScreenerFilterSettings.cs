namespace TelegramBot.Models;

/// <summary>
/// Configurable settings for filtering DexScreener pairs to find the correct token
/// </summary>
public class DexScreenerFilterSettings
{
    /// <summary>
    /// Marketcap tolerance ranges - different tolerance percentages for different marketcap tiers
    /// Format: [MaxMarketcap, TolerancePercentage]
    /// </summary>
    public List<MarketCapToleranceRange> MarketCapToleranceRanges { get; set; } = new()
    {
        // Under 2M: ±500% tolerance (very volatile, price impact heavy)
        new MarketCapToleranceRange { MaxMarketCap = 2_000_000, TolerancePercent = 500 },

        // 2M - 10M: ±300% tolerance
        new MarketCapToleranceRange { MaxMarketCap = 10_000_000, TolerancePercent = 300 },

        // 10M - 50M: ±200% tolerance
        new MarketCapToleranceRange { MaxMarketCap = 50_000_000, TolerancePercent = 200 },

        // Above 50M: ±100% tolerance (more stable)
        new MarketCapToleranceRange { MaxMarketCap = double.MaxValue, TolerancePercent = 100 }
    };

    /// <summary>
    /// Tiered minimum liquidity to marketcap ratio (as percentage), by marketcap tier.
    /// Large caps have naturally lower liq/mc ratios — a flat minimum rejects legit tokens.
    /// </summary>
    public List<MinLiqRatioRange> MinLiquidityToMarketCapRatioRanges { get; set; } = new()
    {
        // Under $2M: 5% min — filters rug pools
        new MinLiqRatioRange { MaxMarketCap = 2_000_000, MinRatioPercent = 5.0 },

        // $2M–$10M: 2% min
        new MinLiqRatioRange { MaxMarketCap = 10_000_000, MinRatioPercent = 2.0 },

        // Above $10M: 0.5% min — just blocks near-zero liq scam pairs
        new MinLiqRatioRange { MaxMarketCap = double.MaxValue, MinRatioPercent = 0.5 }
    };

    /// <summary>
    /// Maximum liquidity to marketcap ratio (as percentage)
    /// Example: 200 = liquidity shouldn't exceed 200% of marketcap
    /// Prevents suspicious pools
    /// </summary>
    public double MaxLiquidityToMarketCapRatioPercent { get; set; } = 200.0;

    /// <summary>
    /// Minimum absolute liquidity in USD
    /// Filters out extremely low liquidity pairs
    /// </summary>
    public double MinAbsoluteLiquidityUsd { get; set; } = 1000.0;

    /// <summary>
    /// Allowed chains (hard filter - only these chains will be accepted)
    /// Supported values: "solana", "bsc", "base"
    /// </summary>
    public List<string> AllowedChains { get; set; } = new()
    {
        "solana",
        "bsc",
        "base"
    };
}

public class MinLiqRatioRange
{
    public double MaxMarketCap { get; set; }
    public double MinRatioPercent { get; set; }
}

/// <summary>
/// Defines a marketcap range and its tolerance percentage
/// </summary>
public class MarketCapToleranceRange
{
    /// <summary>
    /// Maximum marketcap for this range (exclusive upper bound)
    /// </summary>
    public double MaxMarketCap { get; set; }

    /// <summary>
    /// Tolerance percentage for this range
    /// Example: 500 = ±500% (marketcap can be 1/6th to 6x the expected value)
    /// </summary>
    public double TolerancePercent { get; set; }
}
