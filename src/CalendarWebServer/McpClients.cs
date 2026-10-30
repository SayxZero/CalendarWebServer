using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CosmosCalendar.Web;

/// <summary>Who is asking: the name shown to the user and where answers may go.</summary>
public sealed record McpClient(string Id, string Name, IReadOnlyList<string> RedirectUris);

/// <summary>
/// Clients of the OAuth of the MCP mode. Nothing is registered anywhere: a client
/// is either a sealed client_id handed out by Dynamic Client Registration, or the
/// https address of its own metadata document (Client ID Metadata Document, what
/// the MCP specification prefers now), read when it comes.
/// </summary>
public sealed class McpClients
{
    public const string RegisteredPrefix = "cc_";
    private const int MaxMetadataBytes = 64 * 1024;
    private const int MaxCached = 1000;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static readonly string[] ForbiddenSchemes = { "javascript", "data", "file", "vbscript", "about", "blob", "ws", "wss", "ftp" };

    private readonly McpSeal _seal;
    private readonly Func<Uri, CancellationToken, Task<string?>> _fetch;
    private readonly ConcurrentDictionary<string, (McpClient Client, DateTime Until)> _cache = new();

    public McpClients(McpSeal seal, WebOptions options)
    {
        _seal = seal;
        _fetch = options.ClientMetadataFetcher ?? FetchFromInternet;
    }

    /// <summary>The sealed client_id of a client registering itself.</summary>
    public string Register(string name, IReadOnlyList<string> redirectUris) =>
        RegisteredPrefix + _seal.Close("client", new Registered(name, redirectUris.ToArray()));

    public async Task<McpClient?> FindAsync(string? clientId, CancellationToken cancel)
    {
        if (string.IsNullOrEmpty(clientId))
            return null;
        if (clientId.StartsWith(RegisteredPrefix, StringComparison.Ordinal))
        {
            var registered = _seal.Open<Registered>("client", clientId[RegisteredPrefix.Length..]);
            return registered is null ? null : new McpClient(clientId, registered.Name, registered.RedirectUris);
        }
        return await FromMetadataAsync(clientId, cancel);
    }

    /// <summary>
    /// Whether answers may go to @p redirectUri: exactly what the client named, and
    /// on a loopback address any port, since a program on the user's machine takes
    /// whichever port is free (RFC 8252).
    /// </summary>
    public static bool RedirectAllowed(McpClient client, string? redirectUri)
    {
        if (string.IsNullOrEmpty(redirectUri) || !IsAcceptableRedirect(redirectUri))
            return false;
        if (client.RedirectUris.Contains(redirectUri))
            return true;
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var asked) || !IsLoopback(asked))
            return false;
        return client.RedirectUris.Any(known => Uri.TryCreate(known, UriKind.Absolute, out var uri) && IsLoopback(uri)
            && uri.Scheme == asked.Scheme && uri.Host == asked.Host && uri.AbsolutePath == asked.AbsolutePath);
    }

    /// <summary>https; http only to the user's own machine; a program's own scheme, but none that runs anything.</summary>
    public static bool IsAcceptableRedirect(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;
        if (uri.Scheme == Uri.UriSchemeHttp)
            return IsLoopback(uri);
        return !ForbiddenSchemes.Contains(uri.Scheme);
    }

    private static bool IsLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp && (uri.Host is "localhost" or "127.0.0.1" or "[::1]");

    private async Task<McpClient?> FromMetadataAsync(string clientId, CancellationToken cancel)
    {
        // An https address with a path, as the specification says: anything else
        // is not worth a request.
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps
            || address.AbsolutePath.Length <= 1 || !string.IsNullOrEmpty(address.Fragment) || !string.IsNullOrEmpty(address.UserInfo))
            return null;

        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(clientId, out var cached) && cached.Until > now)
            return cached.Client;

        var text = await _fetch(address, cancel);
        if (text is null)
            return null;
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("client_id", out var id) || id.GetString() != clientId
                || !root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
                return null;
            var redirects = uris.EnumerateArray().Select(u => u.GetString() ?? "").ToList();
            if (redirects.Count == 0 || !redirects.All(IsAcceptableRedirect))
                return null;
            var name = root.TryGetProperty("client_name", out var given) ? given.GetString() : null;
            var client = new McpClient(clientId, Tidy(name, address.Host), redirects);
            if (_cache.Count >= MaxCached)
                _cache.Clear();
            _cache[clientId] = (client, now + CacheFor);
            return client;
        }
        catch (Exception failure) when (failure is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The name a client gave itself, short and on one line.</summary>
    public static string Tidy(string? name, string fallback)
    {
        var text = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
            return fallback;
        return text.Length <= 60 ? text : text[..60];
    }

    /// <summary>
    /// Reads the document of a client: an address anybody can name, so it must not
    /// be a way to look inside the network this server stands in.
    /// </summary>
    private static async Task<string?> FetchFromInternet(Uri address, CancellationToken cancel)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                var target = addresses.FirstOrDefault(IsPublic)
                    ?? throw new HttpRequestException("the address of the client is not public");
                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(target, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var answer = await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!answer.IsSuccessStatusCode)
                return null;
            await using var stream = await answer.Content.ReadAsStreamAsync(cancel);
            var buffer = new byte[MaxMetadataBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancel);
                if (read == 0)
                    break;
                total += read;
            }
            return total > MaxMetadataBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or SocketException or IOException)
        {
            return null;
        }
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 0 || bytes[0] == 10 || bytes[0] >= 224
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168));
        }
        // fc00::/7, the unique local addresses.
        return (bytes[0] & 0xfe) != 0xfc;
    }

    private sealed record Registered(string Name, string[] RedirectUris);
}
