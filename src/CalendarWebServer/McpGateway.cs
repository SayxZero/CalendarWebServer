using System.Net;
using System.Net.Http.Headers;

namespace CosmosCalendar.Web;

/// <summary>
/// The door of the MCP mode: requests to /mcp go on to the MCP service
/// (CalendarWebMCP) on the loopback, with the OAuth token opened into what that
/// service works with. The same role the cookie plays for the page: outside there
/// is something the session key cannot be read from, inside a plain Bearer.
/// </summary>
public sealed class McpGateway
{
    /// <summary>A tool waits for the sync server, and may wait for a whole exchange of it.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly HttpClient _client;
    private readonly WebOptions _options;
    private readonly McpOAuth _oauth;
    private readonly ILogger<McpGateway> _log;

    public McpGateway(HttpClient client, WebOptions options, McpOAuth oauth, ILogger<McpGateway> log)
    {
        _client = client;
        _options = options;
        _oauth = oauth;
        _log = log;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";

        // The specification asks for this: a page of another site must not be able
        // to reach a server through the browser of somebody signed in to it.
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && !string.Equals(origin, _options.PublicOrigin(), StringComparison.OrdinalIgnoreCase))
        {
            await ApiProxy.Error(context, 403, "forbidden", "Запрос с чужой страницы");
            return;
        }

        var header = request.Headers.Authorization.ToString();
        var bearer = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var token = _oauth.OpenAccessToken(bearer);
        if (token is null)
        {
            await Unauthorized(context, bearer is null ? "Нужно подключить агента к аккаунту" : "Токен не подходит или устарел", bearer is not null);
            return;
        }

        using var upstream = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(_options.McpUpstreamUri(), "mcp"));
        if (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
        {
            upstream.Content = new StreamContent(request.Body);
            if (MediaTypeHeaderValue.TryParse(request.ContentType, out var type))
                upstream.Content.Headers.ContentType = type;
        }
        foreach (var (name, values) in request.Headers)
        {
            if (name.Equals("Accept", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Last-Event-ID", StringComparison.OrdinalIgnoreCase))
                upstream.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Key);
        upstream.Headers.TryAddWithoutValidation("X-Mcp-Timezone", token.TimeZone);
        upstream.Headers.TryAddWithoutValidation("X-Mcp-Writes", token.Writes ? "1" : "0");
        upstream.Headers.TryAddWithoutValidation("X-Mcp-Client", Uri.EscapeDataString(token.ClientName));

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cancel.CancelAfter(Timeout);
        try
        {
            // Headers first and no buffering: a tool may answer as a stream of events.
            using var answer = await _client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            if (answer.StatusCode == HttpStatusCode.Unauthorized)
            {
                await Unauthorized(context, "Сеанс закончился: подключите агента заново", true);
                return;
            }
            context.Response.StatusCode = (int)answer.StatusCode;
            foreach (var (name, values) in answer.Headers.Concat(answer.Content.Headers))
            {
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) || name.Equals("Allow", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[name] = values.ToArray();
            }
            await using var stream = await answer.Content.ReadAsStreamAsync(cancel.Token);
            await stream.CopyToAsync(context.Response.Body, cancel.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
                await ApiProxy.Error(context, 504, "upstream_timeout", "Служба MCP не ответила вовремя");
        }
        catch (HttpRequestException failure)
        {
            _log.LogWarning("Служба MCP недоступна: {Message}", failure.Message);
            if (!context.Response.HasStarted)
                await ApiProxy.Error(context, 502, "upstream", "Служба MCP недоступна");
        }
    }

    /// <summary>The 401 that shows a client where to get a token (RFC 9728).</summary>
    private Task Unauthorized(HttpContext context, string message, bool invalidToken)
    {
        var metadata = _options.PublicOrigin() + "/.well-known/oauth-protected-resource/mcp";
        context.Response.Headers.WWWAuthenticate = invalidToken
            ? $"Bearer error=\"invalid_token\", resource_metadata=\"{metadata}\", scope=\"{McpOAuth.Scope}\""
            : $"Bearer resource_metadata=\"{metadata}\", scope=\"{McpOAuth.Scope}\"";
        return ApiProxy.Error(context, 401, "unauthorized", message);
    }
}
