using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.Middleware;

// Intercepts POST /Items/{id}/PlaybackInfo -- the one point in Jellyfin's request lifecycle
// where a real, fully-authenticated UserId and a freshly-minted PlaySessionId are both present
// together. Confirmed live 2026-09-01: neither SessionInfo nor PlayerStateInfo exposes
// PlaySessionId server-side, so this is the only source for the mapping StreamKillMiddleware
// (Task 4) needs -- there is no session-registry shortcut.
public class PlaybackInfoKillMappingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IStreamKillRegistry _registry;
    private readonly IAuthorizationContext _authContext;
    private readonly ILogger<PlaybackInfoKillMappingMiddleware> _logger;

    public PlaybackInfoKillMappingMiddleware(
        RequestDelegate next,
        IStreamKillRegistry registry,
        IAuthorizationContext authContext,
        ILogger<PlaybackInfoKillMappingMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _authContext = authContext;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsPlaybackInfoRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        Guid userId;
        try
        {
            var authInfo = await _authContext.GetAuthorizationInfo(context.Request).ConfigureAwait(false);
            userId = authInfo.UserId;
        }
        catch (Exception ex)
        {
            // Fail-open: an unresolved user here just means this PlaySessionId never gets
            // mapped, so it can never be matched as killed -- strictly less capable, never
            // wrong, and never blocks the request itself.
            _logger.LogWarning(ex, "ScheduleEnforcer: failed to resolve auth on PlaybackInfo request");
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Buffer the response so its body can be read after the real handler writes it, then
        // copy it back onto the real response stream -- the only way to inspect a response
        // body from middleware without altering what the client receives.
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);

            buffer.Seek(0, SeekOrigin.Begin);
            var bodyText = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);

            var playSessionId = TryExtractPlaySessionId(bodyText);
            if (playSessionId is not null)
            {
                _registry.RecordPlaySessionOwner(playSessionId, userId, DateTimeOffset.UtcNow);
            }

            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static bool IsPlaybackInfoRequest(HttpRequest request) =>
        request.Method == HttpMethods.Post &&
        request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true;

    private static string? TryExtractPlaySessionId(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            return doc.RootElement.TryGetProperty("PlaySessionId", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
