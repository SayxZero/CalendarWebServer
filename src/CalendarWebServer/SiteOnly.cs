using System.Text.Json;

namespace CosmosCalendar.Web;

/// <summary>
/// The branch site-only: this server shows the presentation page and nothing else.
/// No planner in the browser, no accounts, no MCP, no download, and not one request
/// to a sync server, which need not exist where this runs.
///
/// A list of what is allowed rather than of what is not: a feature that comes with a
/// merge from master stays closed until it is added here. The code of the features
/// stays in place, which keeps those merges small.
/// </summary>
public static class SiteOnly
{
    private static readonly string[] Pages = { "/", "/index.html", "/css/landing.css", "/js/landing.js", "/js/landingPrices.js" };
    private static readonly string[] Folders = { "/img/", "/video/", "/docs/" };
    private static readonly string[] Routes = { "/web/legal", "/web/pricing", "/" + LegalInfo.OfferPath };

    /// <summary>The buttons of the page lead to the planner, and this is what they find.</summary>
    private const string PlannerStub = """
        <!doctype html>
        <html lang="ru">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="color-scheme" content="light dark">
          <meta name="robots" content="noindex">
          <title>Планер в браузере скоро — Космос календарь</title>
          <link rel="icon" href="/img/logo.svg" type="image/svg+xml">
          <link rel="stylesheet" href="/css/landing.css">
        </head>
        <body>
          <main class="wrap" style="min-height:100vh;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;gap:16px">
            <img src="/img/logo.svg" alt="" width="72" height="72" style="border-radius:16px">
            <h1 style="margin:0;font-size:28px">Планер в браузере скоро заработает</h1>
            <p style="margin:0;color:var(--muted)">Мы готовим его к запуску. Загляните немного позже.</p>
            <a class="btn" href="/">На главную</a>
          </main>
        </body>
        </html>
        """;

    public static async Task Gate(HttpContext context, Func<Task> next)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var reading = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);

        if (reading && path == "/web/health")
        {
            // About this server alone: there is no sync server to ask about.
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, mode = "site-only", version = WebServerApp.Version }));
            return;
        }
        if (reading && (path == "/app" || path.StartsWith("/app/", StringComparison.Ordinal)))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync(PlannerStub);
            return;
        }
        if (reading && IsAllowed(path))
        {
            await next();
            return;
        }
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    public static bool IsAllowed(string path) =>
        Pages.Contains(path) || Routes.Contains(path)
        || Folders.Any(folder => path.StartsWith(folder, StringComparison.Ordinal));
}
