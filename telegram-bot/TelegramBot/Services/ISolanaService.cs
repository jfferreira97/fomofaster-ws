using TelegramBot.Models;

namespace TelegramBot.Services;

public class LookupCandidate
{
    public string CA { get; set; } = string.Empty;
    public double MarketCap { get; set; }
    public string? RejectionReason { get; set; } // null = passed filters (winner)
}

public class ContractLookupResult
{
    public string? ContractAddress { get; set; }
    public Chain? Chain { get; set; }
    public ContractAddressSource Source { get; set; }
    public int TimesCacheHit { get; set; }
    public int TimesDexScreenerApiHit { get; set; }
    public int TimesHeliusApiHit { get; set; }
    public TimeSpan LookupDuration { get; set; }
    public List<LookupCandidate>? LookupCandidates { get; set; }
}

public interface ISolanaService
{
    Task<string?> GetContractAddressByTickerAsync(string ticker);
    Task<string?> GetContractAddressByTickerAndMarketCapAsync(string ticker, double? marketCap);
    Task<(string? contractAddress, Chain? chain)> GetContractAddressAndChainByTickerAndMarketCapAsync(string ticker, double? marketCap);
    Task<ContractLookupResult> GetContractAddressWithTrackingAsync(string ticker, double? marketCap);
    void AddToCache(string ticker, string contractAddress);
}
