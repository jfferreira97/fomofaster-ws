using Microsoft.EntityFrameworkCore;
using TelegramBot.Data;
using TelegramBot.Models;
using TelegramBot.Services;
using TelegramBot.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Configure logging with timestamps
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    options.SingleLine = true;
});

// Add services to the container
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR();

// Add database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=fomofaster_ws.db")
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

// Configure CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        });
});

// Register services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ITraderService, TraderService>();
builder.Services.AddSingleton<ITelegramService, TelegramService>();
builder.Services.AddSingleton<ISolanaService, SolanaService>();
builder.Services.AddSingleton<IDexScreenerService, DexScreenerService>();
builder.Services.AddSingleton<ContractAddressRetryService>();
builder.Services.AddSingleton<AppConfigService>();
builder.Services.AddHostedService<TelegramBotPollingService>(); // Background polling service
builder.Services.AddHostedService(provider => provider.GetRequiredService<ContractAddressRetryService>()); // CA retry service
builder.Services.AddSingleton<PaymentPollerService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PaymentPollerService>()); // Solana payment polling + subscription expiry
builder.Services.AddHttpClient(); // For Helius API calls and DexScreener API calls

// Configure settings from appsettings.json or environment variables
builder.Services.Configure<TelegramSettings>(
    builder.Configuration.GetSection("Telegram"));
builder.Services.Configure<HeliusSettings>(
    builder.Configuration.GetSection("Helius"));
builder.Services.Configure<DexScreenerFilterSettings>(
    builder.Configuration.GetSection("DexScreenerFilter"));

// Register Telegram Bot Client
builder.Services.AddSingleton<Telegram.Bot.ITelegramBotClient>(sp =>
{
    var settings = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<TelegramSettings>>().Value;
    if (string.IsNullOrEmpty(settings.BotToken))
    {
        throw new InvalidOperationException("Telegram bot token not configured");
    }
    return new Telegram.Bot.TelegramBotClient(settings.BotToken);
});

var app = builder.Build();

// Apply database migrations automatically
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();
    dbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

// Seed default config values
var appConfigService = app.Services.GetRequiredService<AppConfigService>();
await appConfigService.SeedDefaultsAsync();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Enable static files for dashboard
app.UseStaticFiles();

app.UseCors("AllowAll");
app.UseAuthorization();
app.MapControllers();

// Map SignalR hub
app.MapHub<DashboardHub>("/dashboardHub");

// Dashboard endpoint
app.MapGet("/dashboard", async context =>
{
    context.Response.ContentType = "text/html";
    var html = await System.IO.File.ReadAllTextAsync("wwwroot/dashboard.html");
    await context.Response.WriteAsync(html);
});

app.MapGet("/", () => new { message = "FomoFaster Backend Running", version = "1.0.0" });

app.MapGet("/health", (ITelegramService telegramService) =>
{
    return new
    {
        status = "healthy",
        telegramConfigured = telegramService.IsConfigured()
    };
});

app.MapGet("/bot-info", async (ITelegramService telegramService) =>
{
    try
    {
        var updates = await telegramService.GetUpdatesAsync();
        return Results.Ok(new { status = "success", updates });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { status = "error", message = ex.Message });
    }
});

app.MapPost("/test-bot", async (ITelegramService telegramService, long chatId) =>
{
    try
    {
        await telegramService.SendTestMessageAsync(chatId, "🎉 Bot is working! This is a test message from FOMOFASTER.");
        return Results.Ok(new { status = "success", message = "Test message sent" });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { status = "error", message = ex.Message });
    }
});

// Auto-open dashboard in browser on startup
var dashboardUrl = "http://localhost:8000/dashboard";
try
{
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = dashboardUrl,
        UseShellExecute = true
    });
    Console.WriteLine($"🚀 Dashboard opened at {dashboardUrl}");
}
catch (Exception ex)
{
    Console.WriteLine($"⚠️  Could not auto-open dashboard: {ex.Message}");
}

app.Run();
