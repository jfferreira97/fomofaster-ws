namespace TelegramBot.Models;

public static class ChainInfo
{
    private sealed record Info(int NetworkId, string DexScreenerSlug, string[] Aliases);

    private static readonly Dictionary<Chain, Info> Map = new()
    {
        [Chain.SOL]       = new(1399811149, "solana",    ["sol", "solana"]),
        [Chain.BNB]       = new(56,         "bsc",       ["bnb", "bsc"]),
        [Chain.BASE]      = new(8453,       "base",      ["base"]),
        [Chain.MONAD]     = new(143,        "monad",     ["monad"]),
        [Chain.ETH]       = new(1,          "ethereum",  ["eth", "ethereum"]),
        [Chain.ROBINHOOD] = new(4663,       "robinhood", ["robinhood", "rh"]),
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

    public static string ChainListForHelp() =>
        string.Join(", ", Map.SelectMany(kv => kv.Value.Aliases));
}
