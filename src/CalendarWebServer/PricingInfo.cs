namespace CosmosCalendar.Web;

/// <summary>
/// The prices of the levels of access on the presentation page, from
/// pricing.json in the data folder: the operator's own file, like legal.json,
/// and read on every request for the same reason. Prices are shown as written;
/// an empty one means the page says it comes later.
/// </summary>
public static class PricingInfo
{
    public const string FileName = "pricing.json";

    private static readonly string[] Fields = { "currency", "month", "year" };

    public static void Map(WebApplication app, WebOptions options)
    {
        app.MapGet("/web/pricing", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            var fields = LegalInfo.ReadFields(Path.Combine(options.ResolveDataDir(), FileName), Fields, app.Logger);
            return Results.Json(new { currency = fields["currency"], month = fields["month"], year = fields["year"] });
        });
    }
}
