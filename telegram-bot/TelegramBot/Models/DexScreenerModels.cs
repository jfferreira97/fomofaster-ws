using System.Text.Json.Serialization;

namespace TelegramBot.Models;

// Root response from DexScreener API
public class DexScreenerResponse
{
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("pairs")]
    public List<DexScreenerPair>? Pairs { get; set; }
}

// Individual pair/pool data
public class DexScreenerPair
{
    [JsonPropertyName("chainId")]
    public string? ChainId { get; set; }

    /// <summary>
    /// Parse DexScreener chainId to our Chain enum
    /// DexScreener uses: "solana", "bsc", "base"
    /// We use: SOL, BNB, BASE
    /// </summary>
    public Chain? GetChain()
    {
        return ChainId?.ToLowerInvariant() switch
        {
            "solana" => Chain.SOL,
            "bsc" => Chain.BNB,
            "base" => Chain.BASE,
            _ => null
        };
    }

    [JsonPropertyName("dexId")]
    public string? DexId { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("pairAddress")]
    public string? PairAddress { get; set; }

    [JsonPropertyName("baseToken")]
    public DexScreenerToken? BaseToken { get; set; }

    [JsonPropertyName("quoteToken")]
    public DexScreenerToken? QuoteToken { get; set; }

    [JsonPropertyName("priceNative")]
    public string? PriceNative { get; set; }

    [JsonPropertyName("priceUsd")]
    public string? PriceUsd { get; set; }

    [JsonPropertyName("txns")]
    public DexScreenerTransactions? Txns { get; set; }

    [JsonPropertyName("volume")]
    public DexScreenerVolume? Volume { get; set; }

    [JsonPropertyName("priceChange")]
    public DexScreenerPriceChange? PriceChange { get; set; }

    [JsonPropertyName("liquidity")]
    public DexScreenerLiquidity? Liquidity { get; set; }

    [JsonPropertyName("fdv")]
    public double? Fdv { get; set; }

    [JsonPropertyName("marketCap")]
    public double? MarketCap { get; set; }

    [JsonPropertyName("pairCreatedAt")]
    public long? PairCreatedAt { get; set; }

    [JsonPropertyName("labels")]
    public List<string>? Labels { get; set; }
}

// Token information (baseToken or quoteToken)
public class DexScreenerToken
{
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }
}

// Transaction counts
public class DexScreenerTransactions
{
    [JsonPropertyName("m5")]
    public DexScreenerTxnData? M5 { get; set; }

    [JsonPropertyName("h1")]
    public DexScreenerTxnData? H1 { get; set; }

    [JsonPropertyName("h6")]
    public DexScreenerTxnData? H6 { get; set; }

    [JsonPropertyName("h24")]
    public DexScreenerTxnData? H24 { get; set; }
}

public class DexScreenerTxnData
{
    [JsonPropertyName("buys")]
    public int Buys { get; set; }

    [JsonPropertyName("sells")]
    public int Sells { get; set; }
}

// Volume data
public class DexScreenerVolume
{
    [JsonPropertyName("m5")]
    public double? M5 { get; set; }

    [JsonPropertyName("h1")]
    public double? H1 { get; set; }

    [JsonPropertyName("h6")]
    public double? H6 { get; set; }

    [JsonPropertyName("h24")]
    public double? H24 { get; set; }
}

// Price change percentages
public class DexScreenerPriceChange
{
    [JsonPropertyName("m5")]
    public double? M5 { get; set; }

    [JsonPropertyName("h1")]
    public double? H1 { get; set; }

    [JsonPropertyName("h6")]
    public double? H6 { get; set; }

    [JsonPropertyName("h24")]
    public double? H24 { get; set; }
}

// Liquidity data
public class DexScreenerLiquidity
{
    [JsonPropertyName("usd")]
    public double? Usd { get; set; }

    [JsonPropertyName("base")]
    public double? Base { get; set; }

    [JsonPropertyName("quote")]
    public double? Quote { get; set; }
}
