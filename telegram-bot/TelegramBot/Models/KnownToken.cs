namespace TelegramBot.Models;

public class KnownToken
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string ContractAddress { get; set; } = string.Empty;
    public long MinMarketCap { get; set; }
    public Chain? Chain { get; set; }
}
