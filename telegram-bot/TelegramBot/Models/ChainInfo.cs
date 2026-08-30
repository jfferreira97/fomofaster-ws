namespace TelegramBot.Models;

public static class ChainInfo
{
    // PadreSlug is null until confirmed against padre.gg for that chain — do not guess it,
    // Terminal (Padre) link is omitted for chains where it's null. Confirmed 2026-08-30:
    // Padre has no Monad slug at all; Axiom has no Monad or Base support (AxiomSupported=false).
    private sealed record Info(int NetworkId, string DexScreenerSlug, string[] Aliases, string? PadreSlug = null, bool AxiomSupported = true);

    private static readonly Dictionary<Chain, Info> Map = new()
    {
        [Chain.SOL]       = new(1399811149, "solana",    ["sol", "solana"], PadreSlug: "solana"),
        [Chain.BNB]       = new(56,         "bsc",       ["bnb", "bsc"],    PadreSlug: "bsc"),
        [Chain.BASE]      = new(8453,       "base",      ["base"],         PadreSlug: "base",     AxiomSupported: false),
        [Chain.MONAD]     = new(143,        "monad",     ["monad"],        AxiomSupported: false),
        [Chain.ETH]       = new(1,          "ethereum",  ["eth", "ethereum"], PadreSlug: "eth"),
        [Chain.ROBINHOOD] = new(4663,       "robinhood", ["robinhood", "rh"], PadreSlug: "robinhood"),
    };

    private static readonly Dictionary<int, Chain> ByNetworkId =
        Map.ToDictionary(kv => kv.Value.NetworkId, kv => kv.Key);

    private static readonly Dictionary<string, Chain> ByAlias =
        Map.SelectMany(kv => kv.Value.Aliases.Select(alias => (alias, kv.Key)))
            .ToDictionary(x => x.alias, x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static Chain? FromNetworkId(int networkId) =>
        ByNetworkId.TryGetValue(networkId, out var chain) ? chain : null;

    public static Chain? FromAlias(string alias) =>
        ByAlias.TryGetValue(alias.Trim(), out var chain) ? chain : null;

    public static string DexScreenerUrl(Chain chain, string contractAddress) =>
        $"https://dexscreener.com/{Map[chain].DexScreenerSlug}/{contractAddress}";

    public static string? AxiomUrl(Chain chain, string contractAddress) =>
        Map[chain].AxiomSupported ? $"https://axiom.trade/t/{contractAddress}" : null;

    public static string? PadreUrl(Chain chain, string contractAddress) =>
        Map[chain].PadreSlug is { } slug ? $"https://trade.padre.gg/trade/{slug}/{contractAddress}" : null;

    public static string ChainListForHelp() =>
        string.Join(", ", Map.SelectMany(kv => kv.Value.Aliases));
}
