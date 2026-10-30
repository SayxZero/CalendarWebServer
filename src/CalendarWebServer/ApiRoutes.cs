namespace CosmosCalendar.Web;

/// <summary>How the site treats one route of the sync server.</summary>
public enum RouteKind
{
    /// <summary>Goes through without an account (ping).</summary>
    Open,
    /// <summary>Sign in or sign up: the answer carries the session key, which stays here.</summary>
    SignIn,
    /// <summary>Sign out: the key goes to the server one last time and is forgotten.</summary>
    SignOut,
    /// <summary>Everything else: the key from the cookie goes along as Bearer.</summary>
    Account,
}

/// <summary>One route of the sync server the site lets through.</summary>
public sealed record ApiRoute(string Path, string[] Methods, RouteKind Kind, bool Long = false);

/// <summary>
/// The routes of CalendarServer the browser may reach, and nothing else: the
/// site is a planner like the others, so it gets exactly what a planner uses
/// (DESDOC.md of CosmosCalendar, 3.7). A route the server adds later stays
/// closed here until it is added to this table.
/// </summary>
public static class ApiRoutes
{
    public static readonly IReadOnlyList<ApiRoute> All = new[]
    {
        new ApiRoute("/api/ping", new[] { "GET" }, RouteKind.Open),
        new ApiRoute("/api/register", new[] { "POST" }, RouteKind.SignIn),
        new ApiRoute("/api/login", new[] { "POST" }, RouteKind.SignIn),
        new ApiRoute("/api/logout", new[] { "POST" }, RouteKind.SignOut),
        new ApiRoute("/api/me", new[] { "GET" }, RouteKind.Account),
        new ApiRoute("/api/profile", new[] { "POST" }, RouteKind.Account),
        new ApiRoute("/api/password", new[] { "POST" }, RouteKind.Account),
        new ApiRoute("/api/sessions", new[] { "GET" }, RouteKind.Account),
        new ApiRoute("/api/sessions/revoke", new[] { "POST" }, RouteKind.Account),
        new ApiRoute("/api/sync", new[] { "POST" }, RouteKind.Account, Long: true),
        new ApiRoute("/api/record", new[] { "GET" }, RouteKind.Account),
        new ApiRoute("/api/share", new[] { "GET", "POST" }, RouteKind.Account, Long: true),
        new ApiRoute("/api/share/member", new[] { "POST" }, RouteKind.Account, Long: true),
        new ApiRoute("/api/share/link", new[] { "POST" }, RouteKind.Account, Long: true),
        new ApiRoute("/api/share/peek", new[] { "GET" }, RouteKind.Account),
        new ApiRoute("/api/share/join", new[] { "POST" }, RouteKind.Account, Long: true),
        new ApiRoute("/api/share/leave", new[] { "POST" }, RouteKind.Account, Long: true),
    };

    /// <summary>The route for @p path, without a trailing slash; null when there is none.</summary>
    public static ApiRoute? Find(string path)
    {
        var trimmed = path.Length > 1 ? path.TrimEnd('/') : path;
        foreach (var route in All)
        {
            if (string.Equals(route.Path, trimmed, StringComparison.Ordinal))
                return route;
        }
        return null;
    }
}
