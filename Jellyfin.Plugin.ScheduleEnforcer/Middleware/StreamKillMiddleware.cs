using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.ScheduleEnforcer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ScheduleEnforcer.Middleware;

// Confirmed live 2026-09-01: PlaySessionId (not DeviceId, not the resolved auth UserId -- the
// DirectPlay static route carries no auth header at all) is the one identifier present on
// both DirectPlay ("?static=true&...&playSessionId=...") and Transcode/HLS
// ("&PlaySessionId=...&ApiKey=...") streaming requests. A one-shot abort of whatever's in
// flight is not enough either -- confirmed live that a killed client silently reopens a new
// request unless every SUBSEQUENT matching request is rejected too, which is what
// IsPlaySessionKilled below provides on every request, not just the first sweep.
public class StreamKillMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IStreamKillRegistry _registry;
    private readonly ILogger<StreamKillMiddleware> _logger;

    public StreamKillMiddleware(RequestDelegate next, IStreamKillRegistry registry, ILogger<StreamKillMiddleware> logger)
    {
        _next = next;
        _registry = registry;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsStreamRequest(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var playSessionId = ExtractPlaySessionId(context.Request);
        if (playSessionId is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (_registry.IsPlaySessionKilled(playSessionId))
        {
            _logger.LogWarning("ScheduleEnforcer: rejecting stream request for killed PlaySessionId {PlaySessionId}", playSessionId);
            context.Abort();
            return;
        }

        var trackingId = Guid.NewGuid();
        _registry.TrackActiveRequest(playSessionId, trackingId, context.Abort);
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            _registry.UntrackActiveRequest(playSessionId, trackingId);
        }
    }

    private static bool IsStreamRequest(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return path.Contains("/videos/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/audio/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractPlaySessionId(HttpRequest request)
    {
        // Confirmed live: DirectPlay uses "playSessionId" (lower camel), Transcode/HLS uses
        // "PlaySessionId" (upper camel) -- ASP.NET Core's query collection lookup is
        // case-insensitive by default, so a single lookup covers both.
        return request.Query.TryGetValue("playSessionId", out var value) ? value.ToString() : null;
    }
}
