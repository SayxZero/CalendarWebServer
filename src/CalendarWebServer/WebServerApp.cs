using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace CosmosCalendar.Web;

/// <summary>Builds the site server: options, the proxy, the site's files.</summary>
public static class WebServerApp
{
    /// <summary>Short and long command line switches, onto the "Web" section.</summary>
    public static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["-p"] = "Web:Port",
        ["--port"] = "Web:Port",
        ["--host"] = "Web:Host",
        ["-u"] = "Web:Upstream",
        ["--upstream"] = "Web:Upstream",
        ["-w"] = "Web:WebRoot",
        ["--web-root"] = "Web:WebRoot",
        ["-d"] = "Web:DataDir",
        ["--data-dir"] = "Web:DataDir",
        ["--public-server"] = "Web:PublicServerAddress",
        ["--secure-cookie"] = "Web:SecureCookie",
        ["--trusted-proxy"] = "Web:TrustedProxies:0",
        ["--public-url"] = "Web:PublicUrl",
        ["--mcp-upstream"] = "Web:McpUpstream",
    };

    public static string Version =>
        typeof(WebServerApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0.0";

    /// <summary>
    /// The application, ready to run. @p configure lets the tests put their own
    /// settings over the file and the command line.
    /// </summary>
    public static WebApplication Build(string[] args, Action<WebOptions>? configure = null)
    {
        // The command line is added here, with the switches above: the default
        // reader would reject "-p 8780" as an unknown short switch.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Configuration.AddCommandLine(args, SwitchMappings);

        var options = new WebOptions();
        builder.Configuration.GetSection("Web").Bind(options);
        configure?.Invoke(options);

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = (long)options.MaxBodyMegabytes * 1024 * 1024;
            if (string.IsNullOrWhiteSpace(options.Host))
                kestrel.ListenAnyIP(options.Port);
            else
                kestrel.Listen(IPAddress.Parse(options.Host.Trim()), options.Port);
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        builder.Services.AddSingleton<ApiProxy>();
        if (options.McpEnabled())
        {
            builder.Services.AddSingleton(_ => new McpSeal(options.McpSecret.Trim()));
            builder.Services.AddSingleton<McpClients>();
            builder.Services.AddSingleton<McpOAuth>();
            builder.Services.AddSingleton<McpGateway>();
        }

        var app = builder.Build();
        Configure(app, options);
        return app;
    }

    private static void Configure(WebApplication app, WebOptions options)
    {
        var proxies = options.TrustedProxyAddresses().ToList();
        if (proxies.Count > 0)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            };
            // Only the proxies named in the settings, not the loopback ones the
            // framework trusts by default: a visitor on this very machine must
            // not be able to claim another address.
            forwarded.KnownProxies.Clear();
            forwarded.KnownIPNetworks.Clear();
            foreach (var address in proxies)
                forwarded.KnownProxies.Add(address);
            app.UseForwardedHeaders(forwarded);
        }

        app.Use(SecurityHeaders.Apply);

        var proxy = app.Services.GetRequiredService<ApiProxy>();
        app.Map("/api", api => api.Run(proxy.HandleAsync));

        if (options.McpEnabled())
        {
            McpOAuth.Map(app);
            var gateway = app.Services.GetRequiredService<McpGateway>();
            app.Map("/mcp", mcp => mcp.Run(gateway.HandleAsync));
        }
        else if (!string.IsNullOrWhiteSpace(options.McpUpstream))
        {
            app.Logger.LogWarning("Режим MCP выключен: нужны PublicUrl (адрес сайта) и McpSecret не короче 32 символов");
        }

        app.MapGet("/web/config", () => Results.Json(new
        {
            service = "cosmos-calendar-web",
            version = Version,
            serverAddress = options.ServerAddressForLinks(),
            mcpUrl = options.McpEnabled() ? options.PublicOrigin() + "/mcp" : null,
        }));
        app.MapGet("/web/health", (HttpClient client, CancellationToken cancel) => Health(client, options, cancel));
        LegalInfo.Map(app, options);
        PricingInfo.Map(app, options);
        AppDownloads.Map(app, options);

        var root = ResolveWebRoot(options);
        if (root is null)
        {
            app.Logger.LogWarning("Папка сайта не найдена: укажите её ключом --web-root");
        }
        else
        {
            var files = new PhysicalFileProvider(root);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".js"] = "text/javascript; charset=utf-8";
            types.Mappings[".mjs"] = "text/javascript; charset=utf-8";
            types.Mappings[".webmanifest"] = "application/manifest+json";
            types.Mappings[".md"] = "text/markdown; charset=utf-8";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                ContentTypeProvider = types,
                // Nothing is fingerprinted: the browser asks every time and gets
                // 304 while the file is the same, so a new version shows at once.
                OnPrepareResponse = file => file.Context.Response.Headers.CacheControl = "no-cache",
            });
        }
        app.Logger.LogInformation("Космос календарь: сайт {Version}, порт {Port}", Version, options.Port);
        app.Logger.LogInformation("Сервер синхронизации: {Upstream}", options.UpstreamUri());
        app.Logger.LogInformation("Папка сайта: {Root}", root ?? "(нет)");
        app.Logger.LogInformation("Папка данных: {DataDir}", options.ResolveDataDir());
        if (options.McpEnabled())
            app.Logger.LogInformation("MCP: {Url}, служба {Upstream}", options.PublicOrigin() + "/mcp", options.McpUpstreamUri());
    }

    /// <summary>
    /// Where the site lies: the setting, else "site" next to the program (a
    /// published copy), else CalendarWeb/site of the umbrella checkout, found
    /// by walking up from the program.
    /// </summary>
    public static string? ResolveWebRoot(WebOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WebRoot))
        {
            var given = Path.GetFullPath(options.WebRoot.Trim());
            return File.Exists(Path.Combine(given, "index.html")) ? given : null;
        }
        var beside = Path.Combine(AppContext.BaseDirectory, "site");
        if (File.Exists(Path.Combine(beside, "index.html")))
            return beside;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "CalendarWeb", "site");
            if (File.Exists(Path.Combine(candidate, "index.html")))
                return candidate;
        }
        return null;
    }

    private static async Task<IResult> Health(HttpClient client, WebOptions options, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var answer = await client.GetAsync(new Uri(options.UpstreamUri(), "api/ping"), timeout.Token);
            var text = await answer.Content.ReadAsStringAsync(timeout.Token);
            using var ping = JsonDocument.Parse(text);
            return Results.Json(new { ok = answer.IsSuccessStatusCode, upstream = ping.RootElement.Clone() },
                statusCode: answer.IsSuccessStatusCode ? 200 : 502);
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException or JsonException)
        {
            return Results.Json(new { ok = false, error = "Сервер синхронизации недоступен" }, statusCode: 502);
        }
    }
}
