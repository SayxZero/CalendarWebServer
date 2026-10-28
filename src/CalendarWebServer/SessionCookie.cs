namespace CosmosCalendar.Web;

/// <summary>
/// The session key of the sync server, kept in a cookie the page's scripts
/// cannot read. The planner stores the same key in its settings.ini; a browser
/// has no such place that a script injected into the page could not reach, so
/// the key never leaves this server towards the page at all.
/// </summary>
public static class SessionCookie
{
    public const string Name = "cosmos_session";

    /// <summary>
    /// A key unused for a year stops working on the server (token_lifetime_days),
    /// so the cookie need not outlive it.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public static string? Read(HttpRequest request)
    {
        var value = request.Cookies[Name];
        // A key is 43 characters of base64url; anything else did not come from here.
        if (string.IsNullOrEmpty(value) || value.Length > 256)
            return null;
        foreach (var ch in value)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '='))
                return null;
        }
        return value;
    }

    public static void Write(HttpContext context, string token, WebOptions options)
    {
        context.Response.Cookies.Append(Name, token, Settings(context, options, Lifetime));
    }

    public static void Clear(HttpContext context, WebOptions options)
    {
        context.Response.Cookies.Delete(Name, Settings(context, options, null));
    }

    private static CookieOptions Settings(HttpContext context, WebOptions options, TimeSpan? maxAge)
    {
        var secure = options.SecureCookie.Trim().ToLowerInvariant() switch
        {
            "always" => true,
            "never" => false,
            _ => context.Request.IsHttps,
        };
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            // Strict: no other site can make the browser send it, which is half
            // of the protection against forged requests (the other half is the
            // header ApiProxy demands).
            SameSite = SameSiteMode.Strict,
            Path = "/",
            MaxAge = maxAge,
            IsEssential = true,
        };
    }
}
