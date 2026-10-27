namespace TelegramBot.Models;

public static class ChainInfo
{
    // PadreSlug is null until confirmed against padre.gg for that chain — do not guess it,
    // Terminal (Padre) link is omitted for chains where it's null. Confirmed 2026-08-30:
    // Padre has no Monad slug at all.
    // Same rule for AxiomChain: null until confirmed, because a wrong value ships a dead
    // link to a paying user while a wrong null only omits one. Axiom opens Solana tokens at /t/<mint> and every other chain at
    // /token/<address>?chain=<key> (the owner's own ETH and BNB links, 2026-10-27); the keys come from Axiom's chain selector.
    private sealed record Info(int NetworkId, string DexScreenerSlug, string[] Aliases, string? PadreSlug = null, string? AxiomChain = null);

    private static readonly Dictionary<Chain, Info> Map = new()
    {
        [Chain.SOL]       = new(1399811149, "solana",    ["sol", "solana"], PadreSlug: "solana", AxiomChain: "sol"),
        [Chain.BNB]       = new(56,         "bsc",       ["bnb", "bsc"],    PadreSlug: "bsc", AxiomChain: "bnb"),
        [Chain.BASE]      = new(8453,       "base",      ["base"],         PadreSlug: "base",     AxiomChain: "base"),
        [Chain.MONAD]     = new(143,        "monad",     ["monad"]),
        [Chain.ETH]       = new(1,          "ethereum",  ["eth", "ethereum"], PadreSlug: "eth", AxiomChain: "eth"),
        // Robinhood Chain: DexScreener's slug "robinhood" is confirmed against its token API (2026-10-27), and the owner checked Padre's
        // "robinhood" the same day. Axiom's old /t/<address> link did not open the token; the /token/..?chain= form is the one it uses.
        [Chain.ROBINHOOD] = new(4663,       "robinhood", ["robinhood", "rh"], PadreSlug: "robinhood", AxiomChain: "robinhood"),
        // Arc (Circle's stablecoin-native L1, gas paid in USDC) — mainnet launched 2026-09-16,
        // the same day it started showing up on both feeds. EVM addresses, networkId 5042 on
        // fomo and chainId 5042 on pump. Slug "arc" confirmed against DexScreener's token API
        // (it returns chainId "arc" for these mints), not guessed. Padre still advertises only
        // Solana/Ethereum/BNB/Base and Axiom's Arc support is unconfirmed, so both links stay
        // off until someone checks — DexScreener alone is correct today.
        [Chain.ARC]       = new(5042,       "arc",       ["arc"],          AxiomChain: "arc"),
        // MegaETH: chainId 4326 seen on pump.fun's own stream (trades of FLUFFEY, 2026-10-25) and DexScreener's slug "megaeth" returned by its
        // token API for that mint, so both are measured, not guessed. Padre and Axiom support is unconfirmed: links stay off.
        [Chain.MEGAETH]   = new(4326,       "megaeth",   ["megaeth", "mega"]),
        // HyperEVM (chainId 999) and Ink (57073): both seen on pump.fun's stream with EVM mints, and DexScreener's token API returned the
        // slugs "hyperevm" and "ink" for those mints. Padre / Axiom support unconfirmed: links stay off. X Layer (196) also showed up once,
        // but its token had no DexScreener pair to read a slug from, so it is not added until there is something to confirm it with.
        [Chain.HYPEREVM]  = new(999,        "hyperevm",  ["hyperevm", "hyper"], AxiomChain: "hype"),
        [Chain.INK]       = new(57073,      "ink",       ["ink"],          AxiomChain: "ink"),
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

    public static string DexScreenerSlug(Chain chain) => Map[chain].DexScreenerSlug;

    public static int NetworkIdOf(Chain chain) => Map[chain].NetworkId;

    public static string DexScreenerUrl(Chain chain, string contractAddress) =>
        $"https://dexscreener.com/{Map[chain].DexScreenerSlug}/{contractAddress}";

    public static string? AxiomUrl(Chain chain, string contractAddress) =>
        Map[chain].AxiomChain switch
        {
            null => null,
            "sol" => $"https://axiom.trade/t/{contractAddress}",
            var key => $"https://axiom.trade/token/{contractAddress}?chain={key}"
        };

    public static string? PadreUrl(Chain chain, string contractAddress) =>
        Map[chain].PadreSlug is { } slug ? $"https://trade.padre.gg/trade/{slug}/{contractAddress}" : null;

    public static string ChainListForHelp() =>
        string.Join(", ", Map.SelectMany(kv => kv.Value.Aliases));
}
