using System.Text.Json;
using Microsoft.Extensions.Options;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class DexScreenerService : IDexScreenerService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DexScreenerService> _logger;
    private readonly DexScreenerFilterSettings _filterSettings;
    private const string BaseUrl = "https://api.dexscreener.com/latest/dex/search";

    public DexScreenerService(
        IHttpClientFactory httpClientFactory,
        ILogger<DexScreenerService> logger,
        IOptions<DexScreenerFilterSettings> filterSettings)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _logger = logger;
        _filterSettings = filterSettings.Value;
    }

    public async Task<DexScreenerResponse?> SearchTokenByTickerAsync(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker))
        {
            _logger.LogWarning("Empty ticker provided to DexScreener search");
            return null;
        }

        try
        {
            var url = $"{BaseUrl}?q={Uri.EscapeDataString(ticker)}";
            _logger.LogInformation("Searching DexScreener for ticker: {Ticker}", ticker);

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DexScreener API returned {StatusCode} for ticker {Ticker}",
                    response.StatusCode, ticker);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<DexScreenerResponse>(json);

            if (data?.Pairs != null && data.Pairs.Count > 0)
            {
                _logger.LogInformation("Found {Count} pairs for ticker {Ticker}",
                    data.Pairs.Count, ticker);
            }
            else
            {
                _logger.LogInformation("No pairs found for ticker {Ticker}", ticker);
            }

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling DexScreener API for ticker {Ticker}", ticker);
            return null;
        }
    }

    public async Task<string?> GetContractAddressByTickerAndMarketCapAsync(string ticker, double expectedMarketCap)
    {
        var response = await SearchTokenByTickerAsync(ticker);

        if (response?.Pairs == null || response.Pairs.Count == 0)
        {
            _logger.LogWarning("No pairs found for ticker {Ticker}", ticker);
            return null;
        }

        // Get tolerance percentage for this marketcap
        var tolerancePercent = GetToleranceForMarketCap(expectedMarketCap);
        _logger.LogInformation("Using {Tolerance}% tolerance for marketcap ${MarketCap:N0}",
            tolerancePercent, expectedMarketCap);

        // Calculate bounds
        var minMarketCap = expectedMarketCap / (1 + tolerancePercent / 100.0);
        var maxMarketCap = expectedMarketCap * (1 + tolerancePercent / 100.0);

        // Filter pairs
        var candidates = response.Pairs
            .Where(p => GetRejectionReason(p, minMarketCap, maxMarketCap) == null)
            .OrderByDescending(p => CalculatePairScore(p, expectedMarketCap))
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogWarning("No valid pairs found for ticker {Ticker} with marketcap ${MarketCap:N0} (±{Tolerance}%)",
                ticker, expectedMarketCap, tolerancePercent);
            return null;
        }

        var bestMatch = candidates.First();
        _logger.LogInformation("Selected pair: CA={CA}, MarketCap=${MarketCap:N0}, Liquidity=${Liquidity:N0}, DEX={Dex}, Chain={Chain}",
            bestMatch.BaseToken?.Address, bestMatch.MarketCap, bestMatch.Liquidity?.Usd, bestMatch.DexId, bestMatch.ChainId);

        return bestMatch.BaseToken?.Address;
    }

    public async Task<(string? contractAddress, Chain? chain, List<LookupCandidate> candidates)> GetContractAddressAndChainByTickerAndMarketCapAsync(string ticker, double expectedMarketCap)
    {
        var response = await SearchTokenByTickerAsync(ticker);

        if (response?.Pairs == null || response.Pairs.Count == 0)
        {
            _logger.LogWarning("No pairs found for ticker {Ticker}", ticker);
            return (null, null, new List<LookupCandidate>());
        }

        // Get tolerance percentage for this marketcap
        var tolerancePercent = GetToleranceForMarketCap(expectedMarketCap);
        _logger.LogInformation("Using {Tolerance}% tolerance for marketcap ${MarketCap:N0}",
            tolerancePercent, expectedMarketCap);

        // Calculate bounds
        var minMarketCap = expectedMarketCap / (1 + tolerancePercent / 100.0);
        var maxMarketCap = expectedMarketCap * (1 + tolerancePercent / 100.0);

        // Build diagnostics for all candidates
        var allCandidates = response.Pairs
            .Where(p => p.BaseToken?.Address != null && p.MarketCap != null)
            .Select(p => new LookupCandidate
            {
                CA = p.BaseToken!.Address!,
                MarketCap = p.MarketCap!.Value,
                RejectionReason = GetRejectionReason(p, minMarketCap, maxMarketCap)
            })
            .OrderBy(c => c.MarketCap)
            .ToList();

        var validCandidates = allCandidates
            .Where(c => c.RejectionReason == null)
            .ToList();

        if (validCandidates.Count == 0)
        {
            _logger.LogWarning("No valid pairs found for ticker {Ticker} with marketcap ${MarketCap:N0} (±{Tolerance}%)",
                ticker, expectedMarketCap, tolerancePercent);
            return (null, null, allCandidates);
        }

        // Pick best match by score among valid pairs
        var bestPair = response.Pairs
            .Where(p => validCandidates.Any(c => c.CA == p.BaseToken?.Address))
            .OrderByDescending(p => CalculatePairScore(p, expectedMarketCap))
            .First();

        var contractAddress = bestPair.BaseToken?.Address;
        var chain = MapChainIdToChain(bestPair.ChainId);

        // Mark winner in candidates list
        var winner = allCandidates.FirstOrDefault(c => c.CA == contractAddress);
        if (winner != null)
            winner.RejectionReason = null;

        _logger.LogInformation("Selected pair: CA={CA}, MarketCap=${MarketCap:N0}, Liquidity=${Liquidity:N0}, DEX={Dex}, Chain={Chain}",
            contractAddress, bestPair.MarketCap, bestPair.Liquidity?.Usd, bestPair.DexId, bestPair.ChainId);

        return (contractAddress, chain, allCandidates);
    }

    private Chain? MapChainIdToChain(string? chainId)
    {
        if (string.IsNullOrEmpty(chainId))
            return null;

        return chainId.ToLowerInvariant() switch
        {
            "solana" => Chain.SOL,
            "bsc" => Chain.BNB,
            "base" => Chain.BASE,
            "monad" => Chain.MONAD,
            _ => null
        };
    }

    private double GetToleranceForMarketCap(double marketCap)
    {
        // Loop through tolerance ranges in order
        // Return the tolerance for the first range where marketcap < MaxMarketCap
        foreach (var range in _filterSettings.MarketCapToleranceRanges.OrderBy(r => r.MaxMarketCap))
        {
            if (marketCap < range.MaxMarketCap)
            {
                return range.TolerancePercent;
            }
        }

        // Fallback: use the last range's tolerance (should never hit this if config has double.MaxValue)
        return _filterSettings.MarketCapToleranceRanges.LastOrDefault()?.TolerancePercent ?? 100;
    }

    private string? GetRejectionReason(DexScreenerPair pair, double minMarketCap, double maxMarketCap)
    {
        if (pair.BaseToken?.Address == null || pair.MarketCap == null || pair.Liquidity?.Usd == null)
            return "missing_data";

        if (_filterSettings.AllowedChains.Count > 0 &&
            !_filterSettings.AllowedChains.Contains(pair.ChainId ?? "", StringComparer.OrdinalIgnoreCase))
            return $"wrong_chain:{pair.ChainId}";

        if (pair.MarketCap < minMarketCap || pair.MarketCap > maxMarketCap)
            return $"mc_out_of_range:{minMarketCap:F0}-{maxMarketCap:F0}";

        if (pair.Liquidity.Usd < _filterSettings.MinAbsoluteLiquidityUsd)
            return $"low_liquidity_abs:{pair.Liquidity.Usd:F0}";

        var liqToMcapPercent = (pair.Liquidity.Usd.Value / pair.MarketCap.Value) * 100;
        var minLiqRatio = GetMinLiqRatioForMarketCap(pair.MarketCap.Value);
        if (liqToMcapPercent < minLiqRatio ||
            liqToMcapPercent > _filterSettings.MaxLiquidityToMarketCapRatioPercent)
            return $"liq_ratio:{liqToMcapPercent:F1}%";

        return null;
    }

    private double GetMinLiqRatioForMarketCap(double marketCap)
    {
        foreach (var range in _filterSettings.MinLiquidityToMarketCapRatioRanges.OrderBy(r => r.MaxMarketCap))
        {
            if (marketCap < range.MaxMarketCap)
                return range.MinRatioPercent;
        }
        return _filterSettings.MinLiquidityToMarketCapRatioRanges.LastOrDefault()?.MinRatioPercent ?? 5.0;
    }

    private double CalculatePairScore(DexScreenerPair pair, double expectedMarketCap)
    {
        double score = 0;

        // Prefer pairs with marketcap closer to expected (70% weight)
        var marketCapDiff = Math.Abs((pair.MarketCap ?? 0) - expectedMarketCap);
        var marketCapScore = 1.0 / (1.0 + marketCapDiff / expectedMarketCap);
        score += marketCapScore * 70; // 70 points max

        // Prefer higher liquidity (30% weight)
        var liquidityScore = Math.Log10((pair.Liquidity?.Usd ?? 1) + 1);
        score += liquidityScore * 30; // ~30 points max

        return score;
    }
}
