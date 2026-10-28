using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace CosmosCalendar.Web.Tests;

/// <summary>One request as the stand-in sync server saw it.</summary>
public sealed record Seen(string Method, string PathAndQuery, string? Authorization, string? ForwardedFor, string Body);

/// <summary>
/// The site server on a free port, in front of a stand-in for CalendarServer
/// that answers the planner's routes the way the real one does and remembers
/// what reached it. The client keeps cookies like a browser.
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public const string Token = "tok_0123456789abcdefABCDEF-_";

    public WebApplication Upstream { get; }
    public WebApplication Site { get; }
    public CookieContainer Cookies { get; } = new();
    public HttpClient Client { get; }
    public Uri SiteUri { get; }
    public string WebRoot { get; }

    private readonly ConcurrentQueue<Seen> _seen;

    private Harness(ConcurrentQueue<Seen> seen, WebApplication upstream, WebApplication site, Uri siteUri, string webRoot)
    {
        _seen = seen;
        Upstream = upstream;
        Site = site;
        SiteUri = siteUri;
        WebRoot = webRoot;
        Client = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, UseCookies = true })
        {
            BaseAddress = siteUri,
        };
    }

    /// <summary>
    /// @p upstreamAddress replaces the stand-in with a real server (or with an
    /// address nobody listens on).
    /// </summary>
    public static async Task<Harness> StartAsync(string? upstreamAddress = null, Action<WebOptions>? configure = null)
    {
        var seen = new ConcurrentQueue<Seen>();
        var upstream = BuildUpstream(seen);
        await upstream.StartAsync();
        var upstreamUri = AddressOf(upstream);

        var webRoot = Path.Combine(Path.GetTempPath(), "cosmos-web-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(webRoot, "js"));
        await File.WriteAllTextAsync(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Космос</title>");
        await File.WriteAllTextAsync(Path.Combine(webRoot, "js", "app.js"), "export {};");

        var site = WebServerApp.Build(Array.Empty<string>(), options =>
        {
            options.Host = "127.0.0.1";
            options.Port = 0;
            options.Upstream = upstreamAddress ?? upstreamUri.ToString();
            options.WebRoot = webRoot;
            options.RequestTimeoutSeconds = 5;
            options.SyncTimeoutSeconds = 5;
            configure?.Invoke(options);
        });
        await site.StartAsync();

        return new Harness(seen, upstream, site, AddressOf(site), webRoot);
    }

    /// <summary>What reached the stand-in so far.</summary>
    public List<Seen> Requests() => _seen.ToList();

    public HttpRequestMessage Post(string path, string json, bool csrf = true)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (csrf)
            message.Headers.TryAddWithoutValidation(ApiProxy.CsrfHeader, ApiProxy.CsrfValue);
        return message;
    }

    public Cookie? SessionCookie() =>
        Cookies.GetCookies(SiteUri).Cast<Cookie>().FirstOrDefault(c => c.Name == Web.SessionCookie.Name);

    public async Task SignInAsync()
    {
        using var answer = await Client.SendAsync(Post("/api/login", """{"login":"anna","password":"right"}"""));
        if (answer.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("sign in failed: " + answer.StatusCode);
    }

    private static Uri AddressOf(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        return new Uri(addresses.First().Replace("[::]", "127.0.0.1").Replace("0.0.0.0", "127.0.0.1") + "/");
    }

    private static WebApplication BuildUpstream(ConcurrentQueue<Seen> seen)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();

        app.Run(async context =>
        {
            var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
            var auth = context.Request.Headers.Authorization.ToString();
            seen.Enqueue(new Seen(context.Request.Method, context.Request.Path + context.Request.QueryString,
                string.IsNullOrEmpty(auth) ? null : auth,
                context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } f ? f : null, body));

            var path = context.Request.Path.Value;
            var signedIn = auth == "Bearer " + Token;
            context.Response.ContentType = "application/json";
            switch (path)
            {
                case "/api/ping":
                    await context.Response.WriteAsync("""{"service":"calendar-planner-server","api":1}""");
                    return;
                case "/api/login":
                    if (body.Contains("\"right\""))
                    {
                        await context.Response.WriteAsync("{\"token\":\"" + Token + "\",\"user\":{\"login\":\"anna\",\"displayName\":\"Анна\"}}");
                    }
                    else
                    {
                        context.Response.StatusCode = 401;
                        await context.Response.WriteAsync("""{"error":"unauthorized","message":"Неверный логин или пароль"}""");
                    }
                    return;
                case "/api/register":
                    context.Response.StatusCode = 201;
                    await context.Response.WriteAsync("{\"token\":\"" + Token + "\",\"user\":{\"login\":\"new\"}}");
                    return;
                case "/api/share/leave":
                    context.Response.StatusCode = 503;
                    context.Response.Headers.RetryAfter = "7";
                    await context.Response.WriteAsync("""{"error":"overloaded","message":"Сервер занят"}""");
                    return;
            }
            if (!signedIn)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("""{"error":"unauthorized","message":"Войдите снова"}""");
                return;
            }
            switch (path)
            {
                case "/api/sync":
                    await context.Response.WriteAsync("{\"revision\":1,\"accepted\":0,\"rejected\":0,\"conflicts\":[],\"changes\":[],\"echo\":" + body + "}");
                    return;
                case "/api/record":
                    await context.Response.WriteAsync("""{"found":false}""");
                    return;
                default:
                    await context.Response.WriteAsync("{}");
                    return;
            }
        });
        return app;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Site.StopAsync();
        await Site.DisposeAsync();
        await Upstream.StopAsync();
        await Upstream.DisposeAsync();
        try
        {
            Directory.Delete(WebRoot, true);
        }
        catch (IOException)
        {
        }
    }
}
