using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace CosmosCalendar.Web;

/// <summary>
/// The legal details shown at the bottom of the presentation page, from
/// legal.json in the data folder (<see cref="WebOptions.ResolveDataDir"/>):
/// the operator's own file, which no update of the site or the program
/// overwrites. It is read on every request, so an edit shows without a
/// restart. Next to it lies the file of the offer, named by "offerFile".
/// </summary>
public static class LegalInfo
{
    public const string FileName = "legal.json";

    /// <summary>Where the page links the offer, from the root of the site.</summary>
    public const string OfferPath = "web/legal/offer";

    private static readonly string[] Fields = { "organization", "inn", "ogrnip", "phone", "email", "offerFile", "offerTitle" };

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Policy of the offer's answer: a document, not the page. The page's own
    /// policy has object-src 'none', and that may stop the browser's viewer
    /// of a PDF; scripts stay forbidden all the same.
    /// </summary>
    public const string OfferPolicy =
        "default-src 'none'; object-src 'self'; img-src 'self' data:; style-src 'unsafe-inline'; frame-ancestors 'none'";

    public static void Map(WebApplication app, WebOptions options)
    {
        app.MapGet("/web/legal", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            var fields = Read(options.ResolveDataDir(), app.Logger);
            return Results.Json(new
            {
                organization = fields["organization"],
                inn = fields["inn"],
                ogrnip = fields["ogrnip"],
                phone = fields["phone"],
                email = fields["email"],
                offerUrl = OfferFile(options.ResolveDataDir(), fields["offerFile"]) is null ? "" : OfferPath,
                offerTitle = fields["offerTitle"],
            });
        });

        app.MapGet("/" + OfferPath, (HttpContext context) =>
        {
            var dataDir = options.ResolveDataDir();
            var path = OfferFile(dataDir, Read(dataDir, app.Logger)["offerFile"]);
            if (path is null)
                return Results.Json(new { error = "not_found", message = "Договора оферты нет" }, statusCode: 404);

            new FileExtensionContentTypeProvider().TryGetContentType(path, out var type);
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = OfferPolicy;
            headers.CacheControl = "no-cache";
            // Inline, so a PDF opens in the browser, and under its own name when saved.
            headers.ContentDisposition = new ContentDispositionHeaderValue("inline")
            {
                FileNameStar = Path.GetFileName(path),
            }.ToString();
            return Results.File(path, type ?? "application/octet-stream", enableRangeProcessing: true);
        });
    }

    /// <summary>
    /// The fields of legal.json, every one of them present: missing, empty or
    /// unreadable means "". A number is taken as written (an INN typed without
    /// quotes is still an INN); names are matched regardless of case.
    /// </summary>
    public static Dictionary<string, string> Read(string dataDir, ILogger? log = null)
    {
        return ReadFields(Path.Combine(dataDir, FileName), Fields, log);
    }

    /// <summary>The given fields of one of the operator's JSON files, as described above.</summary>
    public static Dictionary<string, string> ReadFields(string file, string[] fields, ILogger? log = null)
    {
        var result = fields.ToDictionary(name => name, _ => "");
        if (!File.Exists(file))
            return result;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8), Lenient);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("not an object");
            foreach (var property in json.RootElement.EnumerateObject())
            {
                var name = fields.FirstOrDefault(f => string.Equals(f, property.Name, StringComparison.OrdinalIgnoreCase));
                if (name is null)
                    continue;
                result[name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!.Trim(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => "",
                };
            }
        }
        catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException)
        {
            log?.LogWarning("{File} не прочитан: {Message}", file, failure.Message);
        }
        return result;
    }

    /// <summary>
    /// The offer as a full path, or null when it is not named or not there.
    /// Only a file right in the data folder: a name with a folder in it is
    /// refused, so legal.json cannot hand out anything else on the machine.
    /// </summary>
    public static string? OfferFile(string dataDir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name
            || name.Equals(FileName, StringComparison.OrdinalIgnoreCase))
            return null;
        var path = Path.Combine(dataDir, name);
        return File.Exists(path) ? path : null;
    }
}
