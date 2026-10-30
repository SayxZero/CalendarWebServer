using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace CosmosCalendar.Web;

/// <summary>
/// The planner for Android, offered by the presentation page: the APK made by
/// build-packages (CosmosCalendar-&lt;version&gt;.apk) laid into the data folder,
/// next to legal.json, where no update of the site overwrites it. The name says
/// the version, so a new release is a new file and nothing else: the newest one
/// is looked for on every request.
/// </summary>
public static partial class AppDownloads
{
    /// <summary>Where the page links the file, from the root of the site.</summary>
    public const string AndroidPath = "web/download/android";

    public const string ApkType = "application/vnd.android.package-archive";

    [GeneratedRegex(@"^CosmosCalendar-(\d+(?:\.\d+){1,3})\.apk$", RegexOptions.IgnoreCase)]
    private static partial Regex ApkName();

    public static void Map(WebApplication app, WebOptions options)
    {
        app.MapGet("/web/downloads", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            var apk = NewestApk(options.ResolveDataDir());
            return Results.Json(new
            {
                android = apk is null ? null : new { version = apk.Value.Version, url = AndroidPath },
            });
        });

        app.MapGet("/" + AndroidPath, (HttpContext context) =>
        {
            var apk = NewestApk(options.ResolveDataDir());
            if (apk is null)
                return Results.Json(new { error = "not_found", message = "Файла для Android пока нет" }, statusCode: 404);
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = Path.GetFileName(apk.Value.Path),
            }.ToString();
            return Results.File(apk.Value.Path, ApkType, enableRangeProcessing: true);
        });
    }

    /// <summary>The APK with the highest version in the data folder, or null. Versions compare as numbers: 1.10 is newer than 1.9.</summary>
    public static (string Path, string Version)? NewestApk(string dataDir)
    {
        if (!Directory.Exists(dataDir))
            return null;
        (string Path, string Version)? best = null;
        Version? bestVersion = null;
        foreach (var path in Directory.EnumerateFiles(dataDir, "*.apk"))
        {
            var match = ApkName().Match(Path.GetFileName(path));
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
                continue;
            if (bestVersion is null || version > bestVersion)
            {
                best = (path, match.Groups[1].Value);
                bestVersion = version;
            }
        }
        return best;
    }
}
