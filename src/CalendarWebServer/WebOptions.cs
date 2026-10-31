using System.Net;

namespace CosmosCalendar.Web;

/// <summary>
/// Settings of the site server, the "Web" section of appsettings.json; the
/// command line overrides them (see <see cref="WebServerApp.SwitchMappings"/>).
/// </summary>
public sealed class WebOptions
{
    /// <summary>Port the site listens on.</summary>
    public int Port { get; set; } = 8780;

    /// <summary>Address to listen on; empty means every address of the machine.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// The sync server (CalendarServer) the site talks to: "127.0.0.1:8765",
    /// "http://host:port" or "https://host". Its accounts are the accounts of
    /// the site, and its records are what the site shows.
    /// </summary>
    public string Upstream { get; set; } = "127.0.0.1:8765";

    /// <summary>
    /// The address the planners use for the same server, put into the share
    /// links the site hands out (calendar-planner://share?server=...). Empty
    /// means the upstream address as it is - right when both are one machine
    /// on the home network, wrong behind a proxy.
    /// </summary>
    public string PublicServerAddress { get; set; } = "";

    /// <summary>
    /// Folder of the site itself (CalendarWeb/site). Empty means the first of
    /// "site" next to the program and "../CalendarWeb/site" up from it.
    /// </summary>
    public string WebRoot { get; set; } = "";

    /// <summary>
    /// Folder of what the operator puts there and an update must not touch:
    /// legal.json with the legal details of the presentation page and the
    /// file of the offer. Empty means CalendarPlanner/CalendarWeb in the
    /// user's application data (%APPDATA% on Windows, ~/.local/share on
    /// Linux), the way the sync server keeps its own folder.
    /// </summary>
    public string DataDir { get; set; } = "";

    /// <summary>
    /// "auto" marks the session cookie Secure when the request came over HTTPS
    /// (as the proxy in front reports it), "always" and "never" say so outright.
    /// </summary>
    public string SecureCookie { get; set; } = "auto";

    /// <summary>
    /// Proxies whose X-Forwarded-For and X-Forwarded-Proto are believed. Without
    /// them the client is the address the connection came from.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = new();

    /// <summary>How long an exchange may take; the planner waits as long.</summary>
    public int SyncTimeoutSeconds { get; set; } = 60;

    /// <summary>How long any other request may take.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Largest request body; a sync carrying melodies is the big one.</summary>
    public int MaxBodyMegabytes { get; set; } = 64;

    /// <summary>
    /// The address this site is known by from outside, "https://example.org": the
    /// issuer of the OAuth of the MCP mode and the base of its /mcp. Behind a proxy
    /// nothing else can tell it.
    /// </summary>
    public string PublicUrl { get; set; } = "";

    /// <summary>The MCP service (CalendarWebMCP), "127.0.0.1:8790". Empty switches the MCP mode off.</summary>
    public string McpUpstream { get; set; } = "";

    /// <summary>
    /// Seals the tokens of the MCP mode: at least 32 characters, the same across
    /// restarts, or every agent has to connect again. From the settings or the
    /// environment (Web__McpSecret), not the command line, where anybody can read it.
    /// </summary>
    public string McpSecret { get; set; } = "";

    /// <summary>The tests read the documents of clients from a table instead of the Internet.</summary>
    public Func<Uri, CancellationToken, Task<string?>>? ClientMetadataFetcher { get; set; }

    /// <summary>
    /// The branch site-only keeps every feature closed (<see cref="SiteOnly"/>), and no
    /// setting opens them: this is not read from the settings. The tests of the code the
    /// branch shares with master turn it on.
    /// </summary>
    internal bool AllFeaturesForTests { get; set; }

    /// <summary>The MCP mode works only with all of its settings in place.</summary>
    public bool McpEnabled() =>
        AllFeaturesForTests && !string.IsNullOrWhiteSpace(McpUpstream) && McpSecret.Trim().Length >= 32 && PublicOrigin().Length > 0;

    /// <summary>"https://example.org" of <see cref="PublicUrl"/>, empty when it is not a plain site address.</summary>
    public string PublicOrigin()
    {
        var text = PublicUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.AbsolutePath != "/")
            return "";
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public Uri McpUpstreamUri()
    {
        var text = McpUpstream.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        return new Uri(text.TrimEnd('/') + "/");
    }

    /// <summary>The upstream as a base URI, http:// unless it says otherwise.</summary>
    public Uri UpstreamUri()
    {
        var text = Upstream.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;
        return new Uri(text.TrimEnd('/') + "/");
    }

    /// <summary>What the planners call the server: the public address, else the upstream.</summary>
    public string ServerAddressForLinks()
    {
        if (!string.IsNullOrWhiteSpace(PublicServerAddress))
            return PublicServerAddress.Trim();
        var uri = UpstreamUri();
        // The planner writes a plain host:port for its home server and a full
        // URL for anything behind TLS; the link carries it the same way.
        return uri.Scheme == Uri.UriSchemeHttp ? uri.Authority : uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The data folder as a full path; it need not exist.</summary>
    public string ResolveDataDir()
    {
        if (!string.IsNullOrWhiteSpace(DataDir))
            return Path.GetFullPath(DataDir.Trim());
        // Qt's AppDataLocation of the server: Roaming on Windows, the XDG data
        // folder elsewhere.
        var root = Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.ApplicationData
            : Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CalendarPlanner", "CalendarWeb");
    }

    public IEnumerable<IPAddress> TrustedProxyAddresses()
    {
        foreach (var text in TrustedProxies)
        {
            if (IPAddress.TryParse(text.Trim(), out var address))
                yield return address;
        }
    }
}
