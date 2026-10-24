using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TelegramBot.Services;

// Runs around every action of the manage page. After an action that succeeded, it writes one line to the activity history:
// the page being opened, a trader being looked at, or any change (the action's name and the values that were sent).
// The person is identified only by the signed session cookie, like the page itself does. Never changes the response.
public class UserActivityFilter : IAsyncActionFilter
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly WebSessionService _sessions;
    private readonly UserActivityService _activity;

    public UserActivityFilter(WebSessionService sessions, UserActivityService activity)
    {
        _sessions = sessions;
        _activity = activity;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        try
        {
            if (executed.Exception != null && !executed.ExceptionHandled) return;
            var status = executed.Result switch
            {
                Microsoft.AspNetCore.Mvc.ObjectResult o => o.StatusCode ?? 200,
                Microsoft.AspNetCore.Mvc.StatusCodeResult s => s.StatusCode,
                _ => 200
            };
            if (status >= 400) return;

            var name = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName ?? "";
            var isPost = context.HttpContext.Request.Method == "POST";
            string? kind = isPost ? Snake(name) : name switch { "GetMe" => "manage_open", "GetTrader" => "view_trader", _ => null };
            if (kind == null) return;

            var chatId = await _sessions.ValidateTokenAsync(context.HttpContext.Request.Cookies[WebSessionService.CookieName]);
            if (chatId == null) return;

            var args = context.ActionArguments.Where(a => a.Value is not CancellationToken && a.Value is not null)
                .ToDictionary(a => a.Key, a => a.Value);
            _activity.Log(chatId.Value, kind, args.Count == 0 ? null : JsonSerializer.Serialize(args, Json));
        }
        catch { /* the history must never break the page */ }
    }

    // "SetChainDisabled" -> "set_chain_disabled"
    private static string Snake(string s) => string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
