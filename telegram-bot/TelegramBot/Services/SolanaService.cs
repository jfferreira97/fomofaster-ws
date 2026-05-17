using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TelegramBot.Data;
using TelegramBot.Models;

namespace TelegramBot.Services;

public class SolanaService : ISolanaService
{
    private readonly HeliusSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly ILogger<SolanaService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(4);

    public SolanaService(
        IOptions<HeliusSettings> settings,
        IHttpClientFactory httpClientFactory,
        ILogger<SolanaService> logger,
        IServiceProvider serviceProvider)
    {
        _settings = settings.Value;
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(5); // Fast timeout for speed
        _logger = logger;
        _serviceProvider = serviceProvider;

        // Start background task to clean expired cache entries
        Task.Run(CleanupExpiredCacheEntries);
    }

    public async Task<string?> GetContractAddressByTickerAsync(string ticker)
    {
        if (string.IsNullOrEmpty(ticker))
        {
            _logger.LogWarning("Empty ticker provided");
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Check database cache first (case-sensitive: GMONAD != gmonad)
        var cached = await dbContext.CachedTokenAddresses
            .Where(c => EF.Functions.Collate(c.Ticker, "BINARY") == ticker && c.ExpiresAt > DateTime.UtcNow)
            .FirstOrDefaultAsync();

        if (cached != null)
        {
            // Update last accessed time and expiration
            cached.LastAccessed = DateTime.UtcNow;
            cached.ExpiresAt = DateTime.UtcNow.Add(_cacheExpiration);
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("💾 Cache hit for ticker: {Ticker} -> {Address}", ticker, cached.ContractAddress);
            return cached.ContractAddress;
        }

        try
        {
            _logger.LogInformation("🔍 Searching for contract address for ticker: {Ticker}", ticker);

            // Get recent transactions to extract mint addresses
            var txUrl = $"https://api.helius.xyz/v0/addresses/{_settings.FomoAggregatorWallet}/transactions?api-key={_settings.ApiKey}&limit=50";
            var txResponse = await _httpClient.GetAsync(txUrl);

            if (!txResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Helius transactions API returned {StatusCode}", txResponse.StatusCode);
                return null;
            }

            var txBody = await txResponse.Content.ReadAsStringAsync();
            var txDoc = JsonDocument.Parse(txBody);

            // Collect unique mint addresses from recent transactions
            var mintAddresses = new HashSet<string>();
            foreach (var tx in txDoc.RootElement.EnumerateArray())
            {
                if (!tx.TryGetProperty("tokenTransfers", out var tokenTransfers))
                    continue;

                foreach (var transfer in tokenTransfers.EnumerateArray())
                {
                    if (transfer.TryGetProperty("mint", out var mintElement))
                    {
                        var mint = mintElement.GetString();
                        if (!string.IsNullOrEmpty(mint) && mint != "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v") // Skip USDC
                        {
                            mintAddresses.Add(mint);
                        }
                    }
                }
            }

            _logger.LogInformation("Found {Count} unique mint addresses in recent transactions", mintAddresses.Count);

            // Query metadata for each mint to find matching ticker
            foreach (var mint in mintAddresses)
            {
                try
                {
                    var metadataUrl = $"https://api.helius.xyz/v0/token-metadata?api-key={_settings.ApiKey}";
                    var metadataRequest = new
                    {
                        mintAccounts = new[] { mint }
                    };

                    var content = new StringContent(
                        JsonSerializer.Serialize(metadataRequest),
                        Encoding.UTF8,
                        "application/json");

                    var metadataResponse = await _httpClient.PostAsync(metadataUrl, content);
                    if (!metadataResponse.IsSuccessStatusCode)
                        continue;

                    var metadataBody = await metadataResponse.Content.ReadAsStringAsync();
                    var metadataDoc = JsonDocument.Parse(metadataBody);

                    if (metadataDoc.RootElement.GetArrayLength() > 0)
                    {
                        var tokenInfo = metadataDoc.RootElement[0];

                        // Check symbol in onChainMetadata or account.data.parsed.info
                        string? symbol = null;

                        if (tokenInfo.TryGetProperty("onChainMetadata", out var onChainMetadata) &&
                            onChainMetadata.ValueKind != JsonValueKind.Null &&
                            onChainMetadata.TryGetProperty("metadata", out var metadata) &&
                            metadata.ValueKind != JsonValueKind.Null &&
                            metadata.TryGetProperty("data", out var data) &&
                            data.ValueKind != JsonValueKind.Null &&
                            data.TryGetProperty("symbol", out var symbolElement) &&
                            symbolElement.ValueKind != JsonValueKind.Null)
                        {
                            symbol = symbolElement.GetString();
                        }

                        if (!string.IsNullOrEmpty(symbol) && symbol.Equals(ticker, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogInformation("✅ Found contract address: {Address} for ticker: {Ticker}", mint, ticker);

                            // Cache the result in database (Helius is Solana-only)
                            await AddToCacheInternalAsync(dbContext, ticker, mint, Chain.SOL);

                            return mint;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error fetching metadata for mint: {Mint}", mint);
                }
            }

            _logger.LogWarning("❌ No matching contract address found for ticker: {Ticker}", ticker);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching contract address for ticker: {Ticker}", ticker);
            return null;
        }
    }

    public async Task<string?> GetContractAddressByTickerAndMarketCapAsync(string ticker, double? marketCap)
    {
        if (string.IsNullOrEmpty(ticker))
        {
            _logger.LogWarning("Empty ticker provided");
            return null;
        }

        // Strategy: Try DexScreener first, then Helius
        _logger.LogInformation("Attempting CA lookup for {Ticker} (MarketCap: ${MarketCap:N0})", ticker, marketCap);

        // 1. If we have marketcap, try DexScreener first
        if (marketCap.HasValue && marketCap.Value > 0)
        {
            _logger.LogInformation("🔍 Method 1: DexScreener API with marketcap filter");
            using var scope = _serviceProvider.CreateScope();
            var dexScreenerService = scope.ServiceProvider.GetRequiredService<IDexScreenerService>();

            var dexScreenerResult = await dexScreenerService.GetContractAddressByTickerAndMarketCapAsync(ticker, marketCap.Value);
            if (!string.IsNullOrEmpty(dexScreenerResult))
            {
                _logger.LogInformation("✅ DexScreener found CA: {CA}", dexScreenerResult);

                // Cache it for future lookups (chain unknown from this API, will be null)
                using var cacheScope = _serviceProvider.CreateScope();
                var dbContext = cacheScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await AddToCacheInternalAsync(dbContext, ticker, dexScreenerResult, null);

                return dexScreenerResult;
            }
        }
        else
        {
            _logger.LogWarning("⚠️ No marketcap provided, skipping DexScreener lookup");
        }

        // 2. If DexScreener fails, try Helius wallet scanning
        _logger.LogInformation("🔍 Method 2: Helius wallet scanning");
        var heliusResult = await GetContractAddressByTickerAsync(ticker);
        if (!string.IsNullOrEmpty(heliusResult))
        {
            _logger.LogInformation("✅ Helius found CA: {CA}", heliusResult);
            return heliusResult;
        }

        _logger.LogWarning("❌ Both methods failed to find CA for {Ticker}", ticker);
        return null;
    }

    public async Task<(string? contractAddress, Chain? chain)> GetContractAddressAndChainByTickerAndMarketCapAsync(string ticker, double? marketCap)
    {
        if (string.IsNullOrEmpty(ticker))
        {
            _logger.LogWarning("Empty ticker provided");
            return (null, null);
        }

        // Strategy: Try DexScreener first (multi-chain), then Helius (Solana only)
        _logger.LogInformation("Attempting CA lookup for {Ticker} (MarketCap: ${MarketCap:N0})", ticker, marketCap);

        // 1. If we have marketcap, try DexScreener first (multi-chain)
        if (marketCap.HasValue && marketCap.Value > 0)
        {
            _logger.LogInformation("Method 1: DexScreener API with marketcap filter (multi-chain)");
            using var scope = _serviceProvider.CreateScope();
            var dexScreenerService = scope.ServiceProvider.GetRequiredService<IDexScreenerService>();

            var (contractAddress, chain, _) = await dexScreenerService.GetContractAddressAndChainByTickerAndMarketCapAsync(ticker, marketCap.Value);
            if (!string.IsNullOrEmpty(contractAddress))
            {
                _logger.LogInformation("✅ DexScreener found CA: {CA} (Chain: {Chain})", contractAddress, chain);

                // Cache it for future lookups
                using var cacheScope = _serviceProvider.CreateScope();
                var dbContext = cacheScope.ServiceProvider.GetRequiredService<AppDbContext>();
                await AddToCacheInternalAsync(dbContext, ticker, contractAddress, chain);

                return (contractAddress, chain);
            }
        }
        else
        {
            _logger.LogWarning("⚠️ No marketcap provided, skipping DexScreener lookup");
        }

        // 2. If DexScreener fails, try Helius wallet scanning (Solana only)
        _logger.LogInformation("Method 2: Helius wallet scanning");
        var heliusResult = await GetContractAddressByTickerAsync(ticker);
        if (!string.IsNullOrEmpty(heliusResult))
        {
            _logger.LogInformation("✅ Helius found CA: {CA} (Chain: SOL)", heliusResult);
            return (heliusResult, Chain.SOL);
        }

        _logger.LogWarning("❌ Both methods failed to find CA for {Ticker}", ticker);
        return (null, null);
    }

    public async Task<ContractLookupResult> GetContractAddressWithTrackingAsync(string ticker, double? marketCap)
    {
        var startTime = DateTime.UtcNow;
        var result = new ContractLookupResult
        {
            TimesCacheHit = 0,
            TimesDexScreenerApiHit = 0,
            TimesHeliusApiHit = 0
        };

        if (string.IsNullOrEmpty(ticker))
        {
            _logger.LogWarning("Empty ticker provided");
            result.LookupDuration = DateTime.UtcNow - startTime;
            return result;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Check database cache first (case-sensitive: GMONAD != gmonad)
        var cached = await dbContext.CachedTokenAddresses
            .Where(c => EF.Functions.Collate(c.Ticker, "BINARY") == ticker && c.ExpiresAt > DateTime.UtcNow)
            .FirstOrDefaultAsync();

        if (cached != null)
        {
            result.TimesCacheHit = 1;
            result.ContractAddress = cached.ContractAddress;
            result.Chain = cached.Chain ?? Chain.SOL; // Use stored chain, default to SOL for legacy entries
            result.Source = ContractAddressSource.Cache;

            // Update last accessed
            cached.LastAccessed = DateTime.UtcNow;
            cached.ExpiresAt = DateTime.UtcNow.Add(_cacheExpiration);
            await dbContext.SaveChangesAsync();

            _logger.LogInformation("💾 Cache hit for ticker: {Ticker} -> {Address}", ticker, cached.ContractAddress);
            result.LookupDuration = DateTime.UtcNow - startTime;
            return result;
        }

        // Strategy: Try DexScreener first (multi-chain), then Helius (Solana only)
        _logger.LogInformation("Attempting CA lookup for {Ticker} (MarketCap: ${MarketCap:N0})", ticker, marketCap);

        // 1. If we have marketcap, try DexScreener first (multi-chain)
        if (marketCap.HasValue && marketCap.Value > 0)
        {
            _logger.LogInformation("Method 1: DexScreener API with marketcap filter (multi-chain)");
            result.TimesDexScreenerApiHit = 1;

            var dexScreenerService = scope.ServiceProvider.GetRequiredService<IDexScreenerService>();
            var (contractAddress, chain, candidates) = await dexScreenerService.GetContractAddressAndChainByTickerAndMarketCapAsync(ticker, marketCap.Value);

            result.LookupCandidates = candidates;

            if (!string.IsNullOrEmpty(contractAddress))
            {
                _logger.LogInformation("✅ DexScreener found CA: {CA} (Chain: {Chain})", contractAddress, chain);

                result.ContractAddress = contractAddress;
                result.Chain = chain;
                result.Source = ContractAddressSource.DexScreener;

                // Cache it for future lookups (all chains)
                await AddToCacheInternalAsync(dbContext, ticker, contractAddress, chain);

                result.LookupDuration = DateTime.UtcNow - startTime;
                return result;
            }
        }
        else
        {
            _logger.LogWarning("⚠️ No marketcap provided, skipping DexScreener lookup");
        }

        // 2. If DexScreener fails, try Helius wallet scanning (Solana only)
        _logger.LogInformation("Method 2: Helius wallet scanning");
        result.TimesHeliusApiHit = 1;

        var heliusResult = await GetContractAddressByTickerAsync(ticker);
        if (!string.IsNullOrEmpty(heliusResult))
        {
            _logger.LogInformation("✅ Helius found CA: {CA} (Chain: SOL)", heliusResult);

            result.ContractAddress = heliusResult;
            result.Chain = Chain.SOL;
            result.Source = ContractAddressSource.Helius;
            result.LookupDuration = DateTime.UtcNow - startTime;
            return result;
        }

        _logger.LogWarning("❌ Both methods failed to find CA for {Ticker}", ticker);
        result.LookupDuration = DateTime.UtcNow - startTime;
        return result;
    }

    public async void AddToCache(string ticker, string contractAddress)
    {
        if (string.IsNullOrEmpty(ticker) || string.IsNullOrEmpty(contractAddress))
        {
            _logger.LogWarning("Attempted to add invalid ticker or contract address to cache");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await AddToCacheInternalAsync(dbContext, ticker, contractAddress);
        _logger.LogInformation("➕ Manually added to cache: {Ticker} -> {Address}", ticker, contractAddress);
    }

    private async Task AddToCacheInternalAsync(AppDbContext dbContext, string ticker, string contractAddress, Chain? chain = null)
    {
        // Check if already exists (case-sensitive)
        var existing = await dbContext.CachedTokenAddresses
            .Where(c => EF.Functions.Collate(c.Ticker, "BINARY") == ticker)
            .FirstOrDefaultAsync();

        var now = DateTime.UtcNow;
        var expiresAt = now.Add(_cacheExpiration);

        if (existing != null)
        {
            // Update existing
            existing.ContractAddress = contractAddress;
            existing.Chain = chain;
            existing.LastAccessed = now;
            existing.ExpiresAt = expiresAt;
        }
        else
        {
            // Add new
            dbContext.CachedTokenAddresses.Add(new CachedTokenAddress
            {
                Ticker = ticker,
                ContractAddress = contractAddress,
                Chain = chain,
                LastAccessed = now,
                ExpiresAt = expiresAt
            });
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task CleanupExpiredCacheEntries()
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10)); // Check every 10 minutes

                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var now = DateTime.UtcNow;
                var expiredEntries = await dbContext.CachedTokenAddresses
                    .Where(c => c.ExpiresAt <= now)
                    .ToListAsync();

                if (expiredEntries.Count > 0)
                {
                    dbContext.CachedTokenAddresses.RemoveRange(expiredEntries);
                    await dbContext.SaveChangesAsync();

                    _logger.LogInformation("🧹 Cleaned up {Count} expired cache entries", expiredEntries.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during cache cleanup");
            }
        }
    }
}
