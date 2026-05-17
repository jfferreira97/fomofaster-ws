namespace TelegramBot.Models;

public enum ContractAddressSource
{
    Cache = 1,
    DexScreener = 2,
    Helius = 3,
    KnownToken = 4,  // From the hardcoded known tokens list
    WebSocket = 5    // From the FOMO WebSocket feed — CA already resolved by FOMO
}
