using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

/// <summary>The OAuth of the MCP mode and its door, with stand-ins on both sides.</summary>
[TestClass]
public sealed class McpTests
{
    private const string Verifier = "verifier-verifier-verifier-verifier-verifier-0123";
    private const string Public = "https://calendar.example";
    private const string Redirect = "https://agent.example/callback";

    private Harness _h = null!;
    private WebApplication _mcp = null!;
    private readonly ConcurrentQueue<McpSeen> _seen = new();
    private HttpStatusCode _mcpStatus = HttpStatusCode.OK;
    private readonly Dictionary<string, string> _documents = new();
    private HttpClient _plain = null!;

    private sealed record McpSeen(string Method, string? Authorization, string? Timezone, string? Writes, string? Client, string? Protocol, string? Origin, string Body);

    [TestInitialize]
    public async Task Start()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _mcp = builder.Build();
        _mcp.Run(async context =>
        {
            var headers = context.Request.Headers;
            _seen.Enqueue(new McpSeen(context.Request.Method, headers.Authorization, headers["X-Mcp-Timezone"], headers["X-Mcp-Writes"],
                headers["X-Mcp-Client"], headers["MCP-Protocol-Version"], headers.Origin, await new StreamReader(context.Request.Body).ReadToEndAsync()));
            context.Response.StatusCode = (int)_mcpStatus;
            context.Response.ContentType = "application/json";
            context.Response.Headers["Mcp-Test"] = "yes";
            await context.Response.WriteAsync("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        });
        await _mcp.StartAsync();
        var address = _mcp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _h = await Harness.StartAsync(configure: options =>
        {
            options.PublicUrl = Public;
            options.McpUpstream = address;
            options.McpSecret = "a-long-secret-of-the-test-0123456789";
            options.ClientMetadataFetcher = (uri, _) => Task.FromResult(_documents.GetValueOrDefault(uri.ToString()));
        });
        // No cookies and no redirects: an agent is not a browser, and the tests read each step.
        _plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = _h.SiteUri };
    }

    [TestCleanup]
    public async Task Stop()
    {
        _plain.Dispose();
        await _h.DisposeAsync();
        await _mcp.StopAsync();
        await _mcp.DisposeAsync();
    }

    private static string Challenge(string verifier = Verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private async Task<string> RegisterAsync(string name = "Клиент", string redirect = Redirect)
    {
        using var answer = await _plain.PostAsync("/oauth/register", new StringContent(
            JsonSerializer.Serialize(new { client_name = name, redirect_uris = new[] { redirect } }), Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.Created, answer.StatusCode);
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("client_id").GetString()!;
    }

    private static string AuthorizeUrl(string clientId, string redirect = Redirect, string challenge = "") =>
        "/oauth/authorize?response_type=code&state=xyz&code_challenge_method=S256" +
        "&client_id=" + Uri.EscapeDataString(clientId) +
        "&redirect_uri=" + Uri.EscapeDataString(redirect) +
        "&code_challenge=" + (challenge.Length > 0 ? challenge : Challenge());

    /// <summary>The user on the page: answers with the address the page goes on to.</summary>
    private async Task<HttpResponseMessage> ConsentAsync(string request, string password = "right", bool writes = true)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { request, login = "anna", password, timezone = "Europe/Moscow", writes }), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(ApiProxy.CsrfHeader, ApiProxy.CsrfValue);
        return await _plain.SendAsync(message);
    }

    private async Task<string> RequestOfAsync(string clientId, string redirect = Redirect)
    {
        using var answer = await _plain.GetAsync(AuthorizeUrl(clientId, redirect));
        Assert.AreEqual(HttpStatusCode.Redirect, answer.StatusCode);
        var location = answer.Headers.Location!.ToString();
        StringAssert.StartsWith(location, "/connect/?request=");
        return Uri.UnescapeDataString(location["/connect/?request=".Length..]);
    }

    private async Task<string> CodeAsync(string clientId, bool writes = true)
    {
        var request = await RequestOfAsync(clientId);
        using var consent = await ConsentAsync(request, writes: writes);
        Assert.AreEqual(HttpStatusCode.OK, consent.StatusCode);
        using var json = JsonDocument.Parse(await consent.Content.ReadAsStringAsync());
        var redirect = new Uri(json.RootElement.GetProperty("redirect").GetString()!);
        Assert.AreEqual("agent.example", redirect.Host);
        var query = System.Web.HttpUtility.ParseQueryString(redirect.Query);
        Assert.AreEqual("xyz", query["state"]);
        Assert.AreEqual(Public, query["iss"]);
        return query["code"]!;
    }

    private async Task<HttpResponseMessage> TokenAsync(params (string, string)[] form) =>
        await _plain.PostAsync("/oauth/token", new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2))));

    private async Task<(string Access, string Refresh)> TokensAsync(string clientId, bool writes = true)
    {
        var code = await CodeAsync(clientId, writes);
        using var answer = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("client_id", clientId),
            ("redirect_uri", Redirect), ("code_verifier", Verifier));
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        var text = await answer.Content.ReadAsStringAsync();
        Assert.IsFalse(text.Contains(Harness.Token), "the session key stays inside the token");
        using var json = JsonDocument.Parse(text);
        Assert.AreEqual("Bearer", json.RootElement.GetProperty("token_type").GetString());
        return (json.RootElement.GetProperty("access_token").GetString()!, json.RootElement.GetProperty("refresh_token").GetString()!);
    }

    private HttpRequestMessage McpCall(string? token, string? origin = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json"),
        };
        if (token is not null)
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (origin is not null)
            message.Headers.TryAddWithoutValidation("Origin", origin);
        message.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2026-07-28");
        message.Headers.TryAddWithoutValidation("Cookie", "x=1");
        return message;
    }

    [TestMethod]
    public async Task MetadataNamesThePublicAddress()
    {
        using var resource = JsonDocument.Parse(await _plain.GetStringAsync("/.well-known/oauth-protected-resource/mcp"));
        Assert.AreEqual(Public + "/mcp", resource.RootElement.GetProperty("resource").GetString());
        Assert.AreEqual(Public, resource.RootElement.GetProperty("authorization_servers")[0].GetString());

        using var server = JsonDocument.Parse(await _plain.GetStringAsync("/.well-known/oauth-authorization-server"));
        Assert.AreEqual(Public, server.RootElement.GetProperty("issuer").GetString());
        Assert.AreEqual("S256", server.RootElement.GetProperty("code_challenge_methods_supported")[0].GetString());
        Assert.IsTrue(server.RootElement.GetProperty("client_id_metadata_document_supported").GetBoolean());
    }

    [TestMethod]
    public async Task WithoutATokenTheAnswerShowsWhereToGetOne()
    {
        using var answer = await _plain.SendAsync(McpCall(null));
        Assert.AreEqual(HttpStatusCode.Unauthorized, answer.StatusCode);
        StringAssert.Contains(answer.Headers.WwwAuthenticate.ToString(), "resource_metadata=\"" + Public + "/.well-known/oauth-protected-resource/mcp\"");

        using var wrong = await _plain.SendAsync(McpCall("not-a-token"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        StringAssert.Contains(wrong.Headers.WwwAuthenticate.ToString(), "invalid_token");
        Assert.IsTrue(_seen.IsEmpty, "nothing gets as far as the service");
    }

    [TestMethod]
    public async Task TheWholeWayFromRegistrationToTheService()
    {
        var client = await RegisterAsync("Агент Смит");
        var (access, _) = await TokensAsync(client);

        var signIn = _h.Requests().Single(r => r.PathAndQuery == "/api/login");
        StringAssert.Contains(signIn.Body, "ИИ-агент · Агент Смит");

        using var answer = await _plain.SendAsync(McpCall(access));
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual("yes", answer.Headers.GetValues("Mcp-Test").Single());
        Assert.IsTrue(_seen.TryDequeue(out var seen));
        Assert.AreEqual("Bearer " + Harness.Token, seen!.Authorization, "the service gets the key, the agent never does");
        Assert.AreEqual("Europe/Moscow", seen.Timezone);
        Assert.AreEqual("1", seen.Writes);
        Assert.AreEqual("Агент Смит", Uri.UnescapeDataString(seen.Client!));
        Assert.AreEqual("2026-07-28", seen.Protocol);
        StringAssert.Contains(seen.Body, "tools/list");
    }

    [TestMethod]
    public async Task ReadingOnlyGoesWithTheToken()
    {
        var (access, _) = await TokensAsync(await RegisterAsync(), writes: false);
        using var answer = await _plain.SendAsync(McpCall(access));
        Assert.IsTrue(_seen.TryDequeue(out var seen));
        Assert.AreEqual("0", seen!.Writes);
    }

    [TestMethod]
    public async Task ACodeWorksOnceAndOnlyWithItsVerifier()
    {
        var client = await RegisterAsync();
        var code = await CodeAsync(client);
        using var wrong = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("client_id", client),
            ("redirect_uri", Redirect), ("code_verifier", "another-verifier-another-verifier-another-verifier"));
        Assert.AreEqual(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var otherClient = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("client_id", await RegisterAsync("Другой")),
            ("redirect_uri", Redirect), ("code_verifier", Verifier));
        Assert.AreEqual(HttpStatusCode.BadRequest, otherClient.StatusCode);

        using var right = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("client_id", client),
            ("redirect_uri", Redirect), ("code_verifier", Verifier));
        Assert.AreEqual(HttpStatusCode.OK, right.StatusCode);
        using var again = await TokenAsync(("grant_type", "authorization_code"), ("code", code), ("client_id", client),
            ("redirect_uri", Redirect), ("code_verifier", Verifier));
        Assert.AreEqual(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [TestMethod]
    public async Task ARefreshTokenWorksOnceAndIsReplaced()
    {
        var client = await RegisterAsync();
        var (access, refresh) = await TokensAsync(client);
        using var first = await TokenAsync(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", client));
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.AreNotEqual(access, json.RootElement.GetProperty("access_token").GetString());
        Assert.AreNotEqual(refresh, json.RootElement.GetProperty("refresh_token").GetString());
        using var second = await TokenAsync(("grant_type", "refresh_token"), ("refresh_token", refresh), ("client_id", client));
        Assert.AreEqual(HttpStatusCode.BadRequest, second.StatusCode);

        // Nor is one kind of token good for the other.
        using var mixed = await _plain.SendAsync(McpCall(refresh));
        Assert.AreEqual(HttpStatusCode.Unauthorized, mixed.StatusCode);
        using var mixedBack = await TokenAsync(("grant_type", "refresh_token"), ("refresh_token", access), ("client_id", client));
        Assert.AreEqual(HttpStatusCode.BadRequest, mixedBack.StatusCode);
    }

    [TestMethod]
    public async Task AnswersGoOnlyWhereTheClientSaid()
    {
        var client = await RegisterAsync();
        using var elsewhere = await _plain.GetAsync(AuthorizeUrl(client, "https://evil.example/callback"));
        Assert.AreEqual(HttpStatusCode.BadRequest, elsewhere.StatusCode, "no redirect to an address nobody vouched for");

        using var unknown = await _plain.GetAsync(AuthorizeUrl("cc_forged"));
        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

        // Without PKCE the client hears about it, on its own address.
        using var noPkce = await _plain.GetAsync("/oauth/authorize?response_type=code&state=s&client_id=" + Uri.EscapeDataString(client) +
            "&redirect_uri=" + Uri.EscapeDataString(Redirect));
        Assert.AreEqual(HttpStatusCode.Redirect, noPkce.StatusCode);
        StringAssert.StartsWith(noPkce.Headers.Location!.ToString(), Redirect + "?error=invalid_request");

        using var badResource = await _plain.GetAsync(AuthorizeUrl(client) + "&resource=" + Uri.EscapeDataString("https://other.example/mcp"));
        StringAssert.Contains(badResource.Headers.Location!.ToString(), "error=invalid_target");

        // A program on the user's machine takes whichever port is free.
        var local = await RegisterAsync("Локальный", "http://127.0.0.1:1000/cb");
        using var otherPort = await _plain.GetAsync(AuthorizeUrl(local, "http://127.0.0.1:2345/cb"));
        Assert.AreEqual(HttpStatusCode.Redirect, otherPort.StatusCode);

        foreach (var bad in new[] { "http://agent.example/cb", "javascript:alert(1)", "not an address" })
        {
            using var refused = await _plain.PostAsync("/oauth/register", new StringContent(
                JsonSerializer.Serialize(new { client_name = "X", redirect_uris = new[] { bad } }), Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, bad);
        }
    }

    [TestMethod]
    public async Task AClientMayBeItsOwnMetadataDocument()
    {
        const string id = "https://agent.example/oauth/client.json";
        _documents[id] = JsonSerializer.Serialize(new { client_id = id, client_name = "Агент по адресу", redirect_uris = new[] { Redirect } });
        var request = await RequestOfAsync(id);

        using var description = JsonDocument.Parse(await _plain.GetStringAsync("/oauth/request?request=" + Uri.EscapeDataString(request)));
        Assert.AreEqual("Агент по адресу", description.RootElement.GetProperty("client_name").GetString());
        Assert.AreEqual("agent.example", description.RootElement.GetProperty("redirect_host").GetString());

        // A document naming somebody else is no document of this client.
        const string liar = "https://agent.example/oauth/liar.json";
        _documents[liar] = JsonSerializer.Serialize(new { client_id = "https://bank.example/c", client_name = "Банк", redirect_uris = new[] { Redirect } });
        using var refused = await _plain.GetAsync(AuthorizeUrl(liar));
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);

        using var plainHttp = await _plain.GetAsync(AuthorizeUrl("http://agent.example/oauth/client.json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, plainHttp.StatusCode);
    }

    [TestMethod]
    public void TheDocumentOfAClientIsNotFetchedFromInside()
    {
        foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "172.20.1.1", "192.168.0.146", "169.254.169.254", "100.64.0.1", "::1", "fe80::1", "fd00::1", "::ffff:192.168.0.1" })
            Assert.IsFalse(McpClients.IsPublic(IPAddress.Parse(address)), address);
        Assert.IsTrue(McpClients.IsPublic(IPAddress.Parse("93.184.216.34")));
        Assert.IsTrue(McpClients.IsPublic(IPAddress.Parse("2606:2800:220:1::1")));
    }

    [TestMethod]
    public async Task TheUserMaySayNo()
    {
        var request = await RequestOfAsync(await RegisterAsync());
        var message = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { request, deny = true }), Encoding.UTF8, "application/json"),
        };
        message.Headers.TryAddWithoutValidation(ApiProxy.CsrfHeader, ApiProxy.CsrfValue);
        using var answer = await _plain.SendAsync(message);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        StringAssert.StartsWith(json.RootElement.GetProperty("redirect").GetString(), Redirect + "?error=access_denied&state=xyz");
        Assert.IsFalse(_h.Requests().Any(r => r.PathAndQuery == "/api/login"), "nobody signed in");
    }

    [TestMethod]
    public async Task ThePageCountsWrongPasswordsAndProvesItIsThePage()
    {
        var request = await RequestOfAsync(await RegisterAsync());

        var noHeader = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { request, login = "anna", password = "right" }), Encoding.UTF8, "application/json"),
        };
        using var forged = await _plain.SendAsync(noHeader);
        Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.IsFalse(_h.Requests().Any(r => r.PathAndQuery == "/api/login"));

        using var wrong = await ConsentAsync(request, "wrong");
        Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        StringAssert.Contains(await wrong.Content.ReadAsStringAsync(), "Неверный логин");

        for (var i = 1; i < LoginThrottle.PerLogin; ++i)
        {
            using var again = await ConsentAsync(request, "wrong");
        }
        using var blocked = await ConsentAsync(request, "right");
        Assert.AreEqual((HttpStatusCode)429, blocked.StatusCode);
    }

    [TestMethod]
    public async Task TheDoorTurnsAwayOtherSitesAndLostSessions()
    {
        var client = await RegisterAsync();
        var (access, refresh) = await TokensAsync(client);

        using var foreign = await _plain.SendAsync(McpCall(access, "https://evil.example"));
        Assert.AreEqual(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var own = await _plain.SendAsync(McpCall(access, Public));
        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
        Assert.IsTrue(_seen.TryDequeue(out var seen));
        Assert.AreEqual("", seen!.Origin ?? "", "the outside world's Origin stays outside");

        // The session ended on the sync server: the client must go through OAuth again.
        _mcpStatus = HttpStatusCode.Unauthorized;
        using var lost = await _plain.SendAsync(McpCall(access));
        Assert.AreEqual(HttpStatusCode.Unauthorized, lost.StatusCode);
        StringAssert.Contains(lost.Headers.WwwAuthenticate.ToString(), "resource_metadata");
        _mcpStatus = HttpStatusCode.OK;

        using var revoked = await _plain.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("token", refresh) }));
        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode);
        var logout = _h.Requests().Single(r => r.PathAndQuery == "/api/logout");
        Assert.AreEqual("Bearer " + Harness.Token, logout.Authorization);
    }

    [TestMethod]
    public async Task ServiceGoneIsAnAnswerToo()
    {
        var (access, _) = await TokensAsync(await RegisterAsync());
        await _mcp.StopAsync();
        using var gone = await _plain.SendAsync(McpCall(access));
        Assert.AreEqual(HttpStatusCode.BadGateway, gone.StatusCode);
    }

    [TestMethod]
    public async Task WithoutItsSettingsTheModeIsNotThere()
    {
        await using var off = await Harness.StartAsync(configure: options =>
        {
            options.McpUpstream = "127.0.0.1:1";
            options.PublicUrl = Public;
            options.McpSecret = "too short";
        });
        using var client = new HttpClient { BaseAddress = off.SiteUri };
        foreach (var path in new[] { "/.well-known/oauth-authorization-server", "/oauth/authorize", "/mcp" })
        {
            using var answer = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.NotFound, answer.StatusCode, path);
        }
        using var config = JsonDocument.Parse(await client.GetStringAsync("/web/config"));
        Assert.AreEqual(JsonValueKind.Null, config.RootElement.GetProperty("mcpUrl").ValueKind);
    }

    [TestMethod]
    public void ASealedThingOpensOnlyForItsPurposeAndUntouched()
    {
        var seal = new McpSeal("a-long-secret-of-the-test-0123456789");
        var token = new McpToken("id", "key", "client", "name", "UTC", true, 1);
        var text = seal.Close("access", token);
        Assert.AreEqual(token, seal.Open<McpToken>("access", text));
        Assert.IsNull(seal.Open<McpToken>("refresh", text));
        Assert.IsNull(new McpSeal("another-long-secret-of-the-test-012345").Open<McpToken>("access", text));
        Assert.IsNull(seal.Open<McpToken>("access", text[..^4] + (text.EndsWith("AAAA") ? "BBBB" : "AAAA")));
        Assert.IsNull(seal.Open<McpToken>("access", "!!!"));
        Assert.IsFalse(text.Contains("key"), "not readable from outside");
    }
}
