using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.Middleware;

// Intercepts GET/POST /Items/{id}/PlaybackInfo -- the one point in Jellyfin's request lifecycle
// where a real, fully-authenticated UserId and a freshly-minted PlaySessionId are both present
// together. Confirmed live 2026-09-01: neither SessionInfo nor PlayerStateInfo exposes
// PlaySessionId server-side, so this is the only source for the mapping StreamKillMiddleware
// (Task 4) needs -- there is no session-registry shortcut.
public class PlaybackInfoKillMappingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IStreamKillRegistry _registry;
    private readonly ILogger<PlaybackInfoKillMappingMiddleware> _logger;

    public PlaybackInfoKillMappingMiddleware(
        RequestDelegate next,
        IStreamKillRegistry registry,
        ILogger<PlaybackInfoKillMappingMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _logger = logger;
    }

    // IAuthorizationContext is resolved PER REQUEST, not through the constructor: UseMiddleware<T>
    // instantiates this class exactly once at pipeline-build time from the ROOT provider, so a
    // constructor-injected scoped service (which IAuthorizationContext plausibly is -- it fronts
    // user/session/DB lookups) would throw InvalidOperationException at server startup and take
    // down the whole Jellyfin instance, not just this plugin.
    public async Task InvokeAsync(HttpContext context, IAuthorizationContext authContext)
    {
        if (!IsPlaybackInfoRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        Guid userId;
        try
        {
            var authInfo = await authContext.GetAuthorizationInfo(context.Request).ConfigureAwait(false);
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

        // This middleware runs at the OUTERMOST edge of the pipeline (IStartupFilter), i.e.
        // outside Jellyfin's own response-compression middleware. Any client sending
        // "Accept-Encoding: gzip" (every browser) would therefore hand us a gzip-compressed body
        // in the buffer below, JsonDocument.Parse would throw, and no mapping would ever be
        // recorded -- the whole feature silently no-ops. Asking for identity encoding on this one
        // small JSON route keeps the body readable; the response is a few KB, so there is nothing
        // meaningful to save by compressing it.
        context.Request.Headers.AcceptEncoding = "identity";

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

    // Both the GET and the POST form of /Items/{itemId}/PlaybackInfo mint and return a
    // PlaySessionId, so the method is deliberately NOT part of the match -- restricting to POST
    // left any client using the GET form completely unenforceable.
    private static bool IsPlaybackInfoRequest(HttpRequest request) =>
        request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true;

    private string? TryExtractPlaySessionId(string bodyText)
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
        catch (JsonException ex)
        {
            // Logged, not silently swallowed: this is the single most likely failure point in the
            // whole chain (an unexpectedly encoded/compressed body), and a silent null here means
            // the feature no-ops with zero diagnostics during live verification.
            _logger.LogWarning(ex, "ScheduleEnforcer: PlaybackInfo response body was not parseable JSON; no PlaySessionId mapping recorded");
            return null;
        }
    }
}
