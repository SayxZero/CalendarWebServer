using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CosmosCalendar.Web;

/// <summary>What an access or a refresh token holds, sealed: the agent never sees the key inside.</summary>
public sealed record McpToken(string Jti, string Key, string ClientId, string ClientName, string TimeZone, bool Writes, long Expires);

/// <summary>
/// The authorization server of the MCP mode (OAuth 2.1 with PKCE, as the MCP
/// specification requires): an agent asks, the user signs in on a page of this
/// site and says what the agent may do, and the agent gets a token. The password
/// goes to this site and nowhere else.
///
/// The token is the session key of the account, sealed. So the session is an
/// ordinary session of the sync server, named "ИИ-агент · ..." among the others,
/// and it ends the way any session ends; nothing is stored here. Only the codes
/// and the refresh tokens already used are remembered, in memory, so that none of
/// them works twice.
/// </summary>
public sealed class McpOAuth
{
    public const string Scope = "calendar";
    public static readonly TimeSpan RequestLife = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AccessLife = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshLife = TimeSpan.FromDays(90);

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly WebOptions _options;
    private readonly McpSeal _seal;
    private readonly McpClients _clients;
    private readonly HttpClient _http;
    private readonly LoginThrottle _throttle;
    private readonly ILogger<McpOAuth> _log;
    private readonly ConcurrentDictionary<string, long> _used = new();

    public McpOAuth(WebOptions options, McpSeal seal, McpClients clients, HttpClient http, LoginThrottle throttle, ILogger<McpOAuth> log)
    {
        _options = options;
        _seal = seal;
        _clients = clients;
        _http = http;
        _throttle = throttle;
        _log = log;
    }

    private string Issuer => _options.PublicOrigin();
    private string Resource => Issuer + "/mcp";

    public static void Map(WebApplication app)
    {
        var oauth = app.Services.GetRequiredService<McpOAuth>();
        app.MapGet("/.well-known/oauth-protected-resource", oauth.ResourceMetadata);
        app.MapGet("/.well-known/oauth-protected-resource/mcp", oauth.ResourceMetadata);
        app.MapGet("/.well-known/oauth-authorization-server", oauth.ServerMetadata);
        app.MapGet("/.well-known/oauth-authorization-server/mcp", oauth.ServerMetadata);
        app.MapPost("/oauth/register", Answering(oauth.RegisterAsync));
        app.MapGet("/oauth/authorize", Answering(oauth.AuthorizeAsync));
        app.MapPost("/oauth/authorize", Answering(oauth.ConsentAsync));
        app.MapGet("/oauth/request", oauth.DescribeRequest);
        app.MapPost("/oauth/token", Answering(oauth.TokenAsync));
        app.MapPost("/oauth/revoke", Answering(oauth.RevokeAsync));
    }

    /// <summary>
    /// A method taking HttpContext and returning Task&lt;IResult&gt; fits RequestDelegate
    /// too, and that overload wins: the result would be thrown away, and every answer
    /// an empty 200. Named as a Func, it is treated as what it is.
    /// </summary>
    private static Delegate Answering(Func<HttpContext, Task<IResult>> handler) => handler;

    // ---------------------------------------------------------------- metadata

    private IResult ResourceMetadata() => Results.Json(new
    {
        resource = Resource,
        authorization_servers = new[] { Issuer },
        scopes_supported = new[] { Scope },
        bearer_methods_supported = new[] { "header" },
    });

    private IResult ServerMetadata() => Results.Json(new
    {
        issuer = Issuer,
        authorization_endpoint = Issuer + "/oauth/authorize",
        token_endpoint = Issuer + "/oauth/token",
        registration_endpoint = Issuer + "/oauth/register",
        revocation_endpoint = Issuer + "/oauth/revoke",
        scopes_supported = new[] { Scope },
        response_types_supported = new[] { "code" },
        grant_types_supported = new[] { "authorization_code", "refresh_token" },
        code_challenge_methods_supported = new[] { "S256" },
        token_endpoint_auth_methods_supported = new[] { "none" },
        revocation_endpoint_auth_methods_supported = new[] { "none" },
        client_id_metadata_document_supported = true,
        authorization_response_iss_parameter_supported = true,
    });

    // ------------------------------------------------------------ registration

    private async Task<IResult> RegisterAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
        }
        catch (JsonException)
        {
            return OAuthError(400, "invalid_client_metadata", "Это не JSON");
        }
        var uris = (body?["redirect_uris"] as JsonArray)?.Select(u => u?.GetValueKind() == JsonValueKind.String ? u.GetValue<string>() : "").ToList();
        if (uris is null || uris.Count == 0 || uris.Count > 10 || uris.Any(u => u.Length > 2048 || !McpClients.IsAcceptableRedirect(u)))
            return OAuthError(400, "invalid_redirect_uri", "redirect_uris: https, http только на этот компьютер или своя схема программы");

        var given = body!["client_name"];
        var name = McpClients.Tidy(given?.GetValueKind() == JsonValueKind.String ? given.GetValue<string>() : null, "ИИ-агент");
        var id = _clients.Register(name, uris);
        return Results.Json(new
        {
            client_id = id,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            client_name = name,
            redirect_uris = uris,
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        }, JsonOut, statusCode: 201);
    }

    // ----------------------------------------------------------- authorization

    /// <summary>
    /// The agent sends the user here. What it asks for is checked now and sealed,
    /// and the user goes on to the page of the site with it. An error is reported
    /// to the client only once its address is known to be its own: otherwise this
    /// would be a redirect to anywhere for anybody.
    /// </summary>
    private async Task<IResult> AuthorizeAsync(HttpContext context)
    {
        var query = context.Request.Query;
        var client = await _clients.FindAsync(query["client_id"], context.RequestAborted);
        var redirect = query["redirect_uri"].ToString();
        if (client is null)
            return Results.Text("Неизвестный клиент: client_id не найден", "text/plain; charset=utf-8", statusCode: 400);
        if (!McpClients.RedirectAllowed(client, redirect))
            return Results.Text("redirect_uri не совпадает с тем, что назвал клиент", "text/plain; charset=utf-8", statusCode: 400);

        var state = query["state"].ToString();
        if (query["response_type"] != "code")
            return Results.Redirect(WithParameters(redirect, ("error", "unsupported_response_type"), ("state", state), ("iss", Issuer)));
        var challenge = query["code_challenge"].ToString();
        if (query["code_challenge_method"] != "S256" || challenge.Length is < 43 or > 128)
            return Results.Redirect(WithParameters(redirect, ("error", "invalid_request"), ("error_description", "Нужен PKCE S256"), ("state", state), ("iss", Issuer)));
        var resource = query["resource"].ToString();
        if (resource.Length > 0 && resource.TrimEnd('/') != Resource)
            return Results.Redirect(WithParameters(redirect, ("error", "invalid_target"), ("state", state), ("iss", Issuer)));

        var request = _seal.Close("request", new ConsentRequest(client.Id, client.Name, redirect, challenge, state,
            DateTimeOffset.UtcNow.Add(RequestLife).ToUnixTimeSeconds()));
        return Results.Redirect("/connect/?request=" + Uri.EscapeDataString(request));
    }

    /// <summary>What the page of the site shows the user about the request.</summary>
    private IResult DescribeRequest(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = Open(context.Request.Query["request"]);
        if (request is null)
            return Results.Json(new { error = "expired", message = "Запрос устарел: начните подключение в агенте заново" }, JsonOut, statusCode: 400);
        Uri.TryCreate(request.Redirect, UriKind.Absolute, out var uri);
        return Results.Json(new { client_name = request.ClientName, redirect_host = uri?.Host ?? "" }, JsonOut);
    }

    /// <summary>The user signed in on the page and agreed.</summary>
    private async Task<IResult> ConsentAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!ApiProxy.CameFromThisSite(context.Request))
            return Refusal(403, "forbidden", "Запрос пришёл не со страницы сайта");

        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
        }
        catch (JsonException)
        {
            return Refusal(400, "bad_request", "Это не JSON");
        }
        var request = Open(Text(body, "request"));
        if (request is null)
            return Refusal(400, "expired", "Запрос устарел: начните подключение в агенте заново");

        // The user said no: the client hears it, on the address it is known by.
        if (Flag(body, "deny"))
            return Results.Json(new { redirect = WithParameters(request.Redirect, ("error", "access_denied"), ("state", request.State), ("iss", Issuer)) }, JsonOut);

        var login = Text(body, "login");
        var password = Text(body, "password");
        var visitor = context.Connection.RemoteIpAddress;
        var wait = _throttle.RetryAfterSeconds(visitor, login);
        if (wait > 0)
        {
            context.Response.Headers.RetryAfter = wait.ToString();
            return Refusal(429, "throttled", "Слишком много неверных паролей. Попробуйте через несколько минут");
        }

        var key = await SignInAsync(context, login, password, request.ClientName, visitor);
        if (key.Status != 200)
        {
            if (key.Status is 401 or 403)
                _throttle.Fail(visitor, login);
            return Refusal(key.Status, key.Code, key.Message);
        }
        _throttle.Succeed(visitor, login);

        var code = _seal.Close("code", new AuthorizationCode(NewId(), key.Token, request.ClientId, request.ClientName, request.Redirect,
            request.Challenge, TimeZoneOf(Text(body, "timezone")), Flag(body, "writes"), DateTimeOffset.UtcNow.Add(CodeLife).ToUnixTimeSeconds()));
        _log.LogInformation("MCP: агент «{Client}» подключён к аккаунту", request.ClientName);
        return Results.Json(new { redirect = WithParameters(request.Redirect, ("code", code), ("state", request.State), ("iss", Issuer)) }, JsonOut);
    }

    private async Task<(int Status, string Code, string Message, string Token)> SignInAsync(
        HttpContext context, string login, string password, string clientName, System.Net.IPAddress? visitor)
    {
        using var upstream = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.UpstreamUri(), "api/login"));
        upstream.Content = new StringContent(JsonSerializer.Serialize(new { login, password, device = "ИИ-агент · " + clientName }, JsonOut),
            Encoding.UTF8, "application/json");
        if (visitor is not null)
            upstream.Headers.TryAddWithoutValidation("X-Forwarded-For", visitor.ToString());
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cancel.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        try
        {
            using var answer = await _http.SendAsync(upstream, cancel.Token);
            var node = JsonNode.Parse(await answer.Content.ReadAsStringAsync(cancel.Token));
            if (answer.IsSuccessStatusCode)
            {
                var token = Text(node as JsonObject, "token");
                return token.Length > 0
                    ? (200, "", "", token)
                    : (502, "upstream", "Сервер синхронизации ответил без ключа сеанса", "");
            }
            var message = Text(node as JsonObject, "message");
            return ((int)answer.StatusCode, Text(node as JsonObject, "error"), message.Length > 0 ? message : "Не удалось войти", "");
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or JsonException)
        {
            return (502, "upstream", "Сервер синхронизации недоступен", "");
        }
    }

    // ------------------------------------------------------------------ tokens

    private async Task<IResult> TokenAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasFormContentType)
            return OAuthError(400, "invalid_request", "Нужна форма application/x-www-form-urlencoded");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        return form["grant_type"].ToString() switch
        {
            "authorization_code" => ExchangeCode(form),
            "refresh_token" => Refresh(form),
            _ => OAuthError(400, "unsupported_grant_type", "authorization_code или refresh_token"),
        };
    }

    private IResult ExchangeCode(IFormCollection form)
    {
        var code = _seal.Open<AuthorizationCode>("code", form["code"]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (code is null || code.Expires < now || code.ClientId != form["client_id"] || code.Redirect != form["redirect_uri"])
            return OAuthError(400, "invalid_grant", "Код не подходит или устарел");
        var verifier = form["code_verifier"].ToString();
        if (!VerifierMatches(verifier, code.Challenge))
            return OAuthError(400, "invalid_grant", "code_verifier не подходит");
        var resource = form["resource"].ToString();
        if (resource.Length > 0 && resource.TrimEnd('/') != Resource)
            return OAuthError(400, "invalid_target", "Этот сервер выдаёт токены только для своего /mcp");
        if (!UseOnce(code.Jti, code.Expires))
            return OAuthError(400, "invalid_grant", "Код уже использован");
        return Issue(code.Key, code.ClientId, code.ClientName, code.TimeZone, code.Writes);
    }

    private IResult Refresh(IFormCollection form)
    {
        var token = _seal.Open<McpToken>("refresh", form["refresh_token"]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (token is null || token.Expires < now || token.ClientId != form["client_id"])
            return OAuthError(400, "invalid_grant", "Токен обновления не подходит или устарел");
        // Every refresh token works once, as OAuth 2.1 wants for a public client.
        // The list lives in memory: a restart of the server forgets it, and the
        // session behind the token still ends the way any session ends.
        if (!UseOnce(token.Jti, token.Expires))
            return OAuthError(400, "invalid_grant", "Токен обновления уже использован");
        return Issue(token.Key, token.ClientId, token.ClientName, token.TimeZone, token.Writes);
    }

    private IResult Issue(string key, string clientId, string clientName, string timeZone, bool writes)
    {
        var now = DateTimeOffset.UtcNow;
        var access = _seal.Close("access", new McpToken(NewId(), key, clientId, clientName, timeZone, writes, now.Add(AccessLife).ToUnixTimeSeconds()));
        var refresh = _seal.Close("refresh", new McpToken(NewId(), key, clientId, clientName, timeZone, writes, now.Add(RefreshLife).ToUnixTimeSeconds()));
        return Results.Json(new
        {
            access_token = access,
            token_type = "Bearer",
            expires_in = (int)AccessLife.TotalSeconds,
            refresh_token = refresh,
            scope = Scope,
        }, JsonOut);
    }

    /// <summary>The token of a request to /mcp, or null when it is none of ours or too old.</summary>
    public McpToken? OpenAccessToken(string? text)
    {
        var token = _seal.Open<McpToken>("access", text);
        return token is null || token.Expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? null : token;
    }

    /// <summary>A client gives up its token: the session behind it ends on the sync server.</summary>
    private async Task<IResult> RevokeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.HasFormContentType)
            return OAuthError(400, "invalid_request", "Нужна форма application/x-www-form-urlencoded");
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var text = form["token"].ToString();
        var token = _seal.Open<McpToken>("access", text) ?? _seal.Open<McpToken>("refresh", text);
        // An unknown token is answered the same way (RFC 7009): there is nothing to tell.
        if (token is null)
            return Results.Ok();

        using var upstream = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.UpstreamUri(), "api/logout"));
        upstream.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Key);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cancel.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        try
        {
            using var answer = await _http.SendAsync(upstream, cancel.Token);
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException)
        {
            _log.LogWarning("MCP: не удалось завершить сеанс агента: {Message}", failure.Message);
            return OAuthError(503, "temporarily_unavailable", "Сервер синхронизации недоступен");
        }
        return Results.Ok();
    }

    // ----------------------------------------------------------------- helpers

    private ConsentRequest? Open(string? text)
    {
        var request = _seal.Open<ConsentRequest>("request", text);
        return request is null || request.Expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? null : request;
    }

    private bool UseOnce(string jti, long expires)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var pair in _used)
        {
            if (pair.Value < now)
                _used.TryRemove(pair.Key, out _);
        }
        return _used.TryAdd(jti, expires);
    }

    private static bool VerifierMatches(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128)
            return false;
        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>A name of the IANA list goes into a header later, so only its alphabet passes.</summary>
    private static string TimeZoneOf(string text) =>
        text.Length is > 0 and <= 64 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+') ? text : "UTC";

    private static string Text(JsonObject? body, string name)
    {
        var node = body?[name];
        return node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : "";
    }

    private static bool Flag(JsonObject? body, string name) => body?[name]?.GetValueKind() == JsonValueKind.True;

    internal static string WithParameters(string address, params (string Name, string Value)[] parameters)
    {
        var text = new StringBuilder(address);
        var separator = address.Contains('?') ? '&' : '?';
        foreach (var (name, value) in parameters)
        {
            if (value.Length == 0)
                continue;
            text.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }
        return text.ToString();
    }

    private static IResult Refusal(int status, string code, string message) =>
        Results.Json(new { error = code, message }, JsonOut, statusCode: status);

    private static IResult OAuthError(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, JsonOut, statusCode: status);

    private sealed record ConsentRequest(string ClientId, string ClientName, string Redirect, string Challenge, string State, long Expires);

    private sealed record AuthorizationCode(string Jti, string Key, string ClientId, string ClientName, string Redirect, string Challenge,
        string TimeZone, bool Writes, long Expires);
}
