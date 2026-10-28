using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CosmosCalendar.Web;

/// <summary>
/// Passes the browser's /api/* requests on to the sync server.
///
/// The site is one more device of the account: it speaks the planner's own
/// protocol, and the server keeps every rule of it - revisions, the winner of
/// two edits, shared calendars and their conflicts, wrong passwords. This class
/// only adds what a browser needs in front of that: the session key lives in
/// an HttpOnly cookie instead of the page, a request that changes something
/// has to prove it came from the site's own page, and the routes are a fixed
/// list (<see cref="ApiRoutes"/>).
/// </summary>
public sealed class ApiProxy
{
    /// <summary>
    /// The header every request that changes something must carry. A page of
    /// another site cannot set it without the browser asking this server first
    /// (CORS preflight), and this server never says yes.
    /// </summary>
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "CosmosCalendarWeb";

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly HttpClient _client;
    private readonly WebOptions _options;
    private readonly LoginThrottle _throttle;
    private readonly ILogger<ApiProxy> _log;

    public ApiProxy(HttpClient client, WebOptions options, LoginThrottle throttle, ILogger<ApiProxy> log)
    {
        _client = client;
        _options = options;
        _throttle = throttle;
        _log = log;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var watch = Stopwatch.StartNew();
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";

        // Mapped under /api, the path arrives split into PathBase and the rest.
        var path = (request.PathBase + request.Path).Value ?? "";
        var route = ApiRoutes.Find(path);
        if (route is null)
        {
            await Error(context, 404, "not_found", "Неизвестный запрос");
            Log(context, watch);
            return;
        }
        var method = request.Method == "HEAD" ? "GET" : request.Method;
        if (!route.Methods.Contains(method))
        {
            context.Response.Headers.Allow = string.Join(", ", route.Methods);
            await Error(context, 405, "method", "Этот запрос так не делается");
            Log(context, watch);
            return;
        }
        if (method != "GET" && !CameFromThisSite(request))
        {
            await Error(context, 403, "forbidden", "Запрос пришёл не со страницы сайта");
            Log(context, watch);
            return;
        }

        var token = SessionCookie.Read(request);
        if (route.Kind == RouteKind.Account && token is null)
        {
            await Error(context, 401, "unauthorized", "Войдите в аккаунт");
            Log(context, watch);
            return;
        }

        byte[] body = Array.Empty<byte>();
        if (method != "GET")
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, context.RequestAborted);
            body = buffer.ToArray();
        }

        string login = "";
        if (route.Kind == RouteKind.SignIn)
        {
            login = ReadLogin(body);
            var wait = _throttle.RetryAfterSeconds(context.Connection.RemoteIpAddress, login);
            if (wait > 0)
            {
                context.Response.Headers.RetryAfter = wait.ToString();
                await Error(context, 429, "throttled",
                    "Слишком много неверных паролей. Попробуйте через несколько минут");
                Log(context, watch);
                return;
            }
        }

        using var upstream = new HttpRequestMessage(new HttpMethod(method),
            new Uri(_options.UpstreamUri(), path.TrimStart('/') + request.QueryString.Value));
        if (method != "GET")
        {
            upstream.Content = new ByteArrayContent(body);
            upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        if (token is not null && route.Kind != RouteKind.SignIn)
            upstream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        upstream.Headers.Accept.ParseAdd("application/json");
        // The server does not read these yet; a version that learns to count
        // wrong passwords per visitor behind a proxy will find them here.
        var client = context.Connection.RemoteIpAddress;
        if (client is not null)
            upstream.Headers.TryAddWithoutValidation("X-Forwarded-For", client.ToString());
        upstream.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);

        HttpResponseMessage answer;
        var timeout = TimeSpan.FromSeconds(route.Long ? _options.SyncTimeoutSeconds : _options.RequestTimeoutSeconds);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cancel.CancelAfter(timeout);
        try
        {
            answer = await _client.SendAsync(upstream, HttpCompletionOption.ResponseContentRead, cancel.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            await Error(context, 504, "upstream_timeout",
                $"Сервер синхронизации не ответил за {(int)timeout.TotalSeconds} с");
            Log(context, watch);
            return;
        }
        catch (HttpRequestException failure)
        {
            _log.LogWarning("Сервер синхронизации недоступен: {Message}", failure.Message);
            await Error(context, 502, "upstream", "Сервер синхронизации недоступен");
            Log(context, watch);
            return;
        }

        using (answer)
        {
            var payload = await answer.Content.ReadAsByteArrayAsync(context.RequestAborted);
            var status = (int)answer.StatusCode;

            switch (route.Kind)
            {
                case RouteKind.SignIn:
                    if (status is 200 or 201)
                    {
                        _throttle.Succeed(client, login);
                        payload = KeepTokenHere(context, payload, out var issued);
                        if (issued is null)
                        {
                            await Error(context, 502, "upstream", "Сервер синхронизации ответил без ключа сеанса");
                            Log(context, watch);
                            return;
                        }
                    }
                    else if (status == 401 || status == 403)
                    {
                        _throttle.Fail(client, login);
                    }
                    break;
                case RouteKind.SignOut:
                    // Signed out here whatever the server says: a key it no
                    // longer knows is as good as forgotten.
                    SessionCookie.Clear(context, _options);
                    break;
                case RouteKind.Account:
                    // The key stopped working (signed out elsewhere, password
                    // changed, a year unused, account deleted): forget it, and
                    // the page asks to sign in again, as the planner does.
                    if (status == 401)
                        SessionCookie.Clear(context, _options);
                    break;
            }

            context.Response.StatusCode = status;
            CopyHeader(answer, context, "Retry-After");
            CopyHeader(answer, context, "Allow");
            if (request.Method != "HEAD" && payload.Length > 0)
            {
                context.Response.ContentType = answer.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
                await context.Response.Body.WriteAsync(payload, context.RequestAborted);
            }
        }
        Log(context, watch);
    }

    /// <summary>
    /// A request that changes something must carry the site's header, and an
    /// Origin, when the browser sends one, must be this very site.
    /// </summary>
    private static bool CameFromThisSite(HttpRequest request)
    {
        if (!string.Equals(request.Headers[CsrfHeader], CsrfValue, StringComparison.Ordinal))
            return false;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
            return true;
        return string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLogin(byte[] body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            return node?["login"]?.GetValue<string>() ?? "";
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return "";
        }
    }

    /// <summary>
    /// Takes the session key out of a sign-in answer into the cookie; the page
    /// gets the rest of the answer (the account) and nothing it could leak.
    /// </summary>
    private byte[] KeepTokenHere(HttpContext context, byte[] payload, out string? token)
    {
        token = null;
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject answer)
                return payload;
            token = answer["token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token))
            {
                token = null;
                return payload;
            }
            answer.Remove("token");
            SessionCookie.Write(context, token, _options);
            return Encoding.UTF8.GetBytes(answer.ToJsonString(JsonOut));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            token = null;
            return payload;
        }
    }

    private static void CopyHeader(HttpResponseMessage answer, HttpContext context, string name)
    {
        if (answer.Headers.TryGetValues(name, out var values) || answer.Content.Headers.TryGetValues(name, out values))
            context.Response.Headers[name] = values.ToArray();
    }

    public static Task Error(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        var text = JsonSerializer.Serialize(new { error = code, message }, JsonOut);
        return context.Response.WriteAsync(text, Encoding.UTF8);
    }

    private void Log(HttpContext context, Stopwatch watch)
    {
        // Path only: a query may carry a share token, and tokens stay out of logs.
        _log.LogInformation("{Client} {Method} {Path} {Status} {Ms} мс",
            context.Connection.RemoteIpAddress, context.Request.Method, (context.Request.PathBase + context.Request.Path).Value,
            context.Response.StatusCode, watch.ElapsedMilliseconds);
    }
}
