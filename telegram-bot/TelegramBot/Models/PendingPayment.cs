namespace TelegramBot.Models;

public class PendingPayment
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public string WalletPublicKey { get; set; } = string.Empty;
    public string WalletPrivateKey { get; set; } = string.Empty;
    public decimal AmountSol { get; set; } = 0.2m;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsConfirmed { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}
