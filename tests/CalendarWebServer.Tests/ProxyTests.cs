using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

[TestClass]
public sealed class ProxyTests
{
    private Harness _h = null!;

    [TestInitialize]
    public async Task Start() => _h = await Harness.StartAsync();

    [TestCleanup]
    public async Task Stop() => await _h.DisposeAsync();

    [TestMethod]
    public async Task SignInKeepsTheKeyInAnHttpOnlyCookie()
    {
        using var answer = await _h.Client.SendAsync(_h.Post("/api/login", """{"login":"anna","password":"right"}"""));
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);

        var text = await answer.Content.ReadAsStringAsync();
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("token"));
        using var json = JsonDocument.Parse(text);
        Assert.AreEqual("anna", json.RootElement.GetProperty("user").GetProperty("login").GetString());

        var header = string.Join("; ", answer.Headers.GetValues("Set-Cookie"));
        StringAssert.Contains(header.ToLowerInvariant(), "httponly");
        StringAssert.Contains(header.ToLowerInvariant(), "samesite=strict");
        Assert.AreEqual(Harness.Token, _h.SessionCookie()?.Value);
    }

    [TestMethod]
    public async Task RegistrationSignsInToo()
    {
        using var answer = await _h.Client.SendAsync(_h.Post("/api/register", """{"login":"new","password":"secret1"}"""));
        Assert.AreEqual(HttpStatusCode.Created, answer.StatusCode);
        Assert.AreEqual(Harness.Token, _h.SessionCookie()?.Value);
    }

    [TestMethod]
    public async Task AccountRequestsCarryTheKeyAsBearer()
    {
        await _h.SignInAsync();
        const string body = """{"since":0,"device":"Сайт","changes":[{"kind":"event","id":"e1","modified":5,"deleted":false,"data":{"title":"Встреча"}}]}""";
        using var answer = await _h.Client.SendAsync(_h.Post("/api/sync", body));
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);

        var sync = _h.Requests().Single(r => r.PathAndQuery == "/api/sync");
        Assert.AreEqual("Bearer " + Harness.Token, sync.Authorization);
        Assert.AreEqual(body, sync.Body, "the body reaches the server byte for byte");
        Assert.AreEqual("127.0.0.1", sync.ForwardedFor);

        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        Assert.AreEqual("Встреча", json.RootElement.GetProperty("echo").GetProperty("changes")[0]
            .GetProperty("data").GetProperty("title").GetString());
    }

    [TestMethod]
    public async Task QueryStringGoesThroughAsItIs()
    {
        await _h.SignInAsync();
        using var answer = await _h.Client.GetAsync("/api/record?kind=task&id=a%2Fb%20c");
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual("/api/record?kind=task&id=a%2Fb%20c", _h.Requests().Single(r => r.PathAndQuery.StartsWith("/api/record")).PathAndQuery);
    }

    [TestMethod]
    public async Task WithoutTheCookieNothingReachesTheServer()
    {
        using var answer = await _h.Client.SendAsync(_h.Post("/api/sync", "{}"));
        Assert.AreEqual(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.AreEqual(0, _h.Requests().Count);
        StringAssert.Contains(await answer.Content.ReadAsStringAsync(), "\"error\":\"unauthorized\"");
    }

    [TestMethod]
    public async Task ABearerFromThePageIsIgnored()
    {
        var request = _h.Post("/api/sync", "{}");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Harness.Token);
        using var answer = await _h.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.AreEqual(0, _h.Requests().Count);
    }

    [TestMethod]
    public async Task AChangeWithoutTheSiteHeaderIsRefused()
    {
        await _h.SignInAsync();
        using var answer = await _h.Client.SendAsync(_h.Post("/api/sync", "{}", csrf: false));
        Assert.AreEqual(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.IsFalse(_h.Requests().Any(r => r.PathAndQuery == "/api/sync"));
    }

    [TestMethod]
    public async Task AChangeFromAnotherOriginIsRefused()
    {
        await _h.SignInAsync();
        var request = _h.Post("/api/sync", "{}");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var answer = await _h.Client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Forbidden, answer.StatusCode);

        var own = _h.Post("/api/sync", "{}");
        own.Headers.TryAddWithoutValidation("Origin", _h.SiteUri.GetLeftPart(UriPartial.Authority));
        using var ownAnswer = await _h.Client.SendAsync(own);
        Assert.AreEqual(HttpStatusCode.OK, ownAnswer.StatusCode);
    }

    [TestMethod]
    public async Task OnlyThePlannersRoutesExist()
    {
        await _h.SignInAsync();
        using (var unknown = await _h.Client.GetAsync("/api/admin/users"))
            Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var wrong = await _h.Client.GetAsync("/api/sync"))
        {
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, wrong.StatusCode);
            Assert.AreEqual("POST", string.Join(",", wrong.Content.Headers.Allow));
        }
        Assert.IsFalse(_h.Requests().Any(r => r.PathAndQuery.StartsWith("/api/admin") || r.Method == "GET" && r.PathAndQuery == "/api/sync"));
    }

    [TestMethod]
    public async Task PingNeedsNoAccount()
    {
        using var answer = await _h.Client.GetAsync("/api/ping");
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.IsNull(_h.Requests().Single().Authorization);
    }

    [TestMethod]
    public async Task AKeyTheServerRefusesIsForgotten()
    {
        _h.Cookies.Add(_h.SiteUri, new Cookie(SessionCookie.Name, "expired_key"));
        using var answer = await _h.Client.GetAsync("/api/me");
        Assert.AreEqual(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.IsNull(_h.SessionCookie(), "the cookie is cleared");
    }

    [TestMethod]
    public async Task SignOutForgetsTheKey()
    {
        await _h.SignInAsync();
        using var answer = await _h.Client.SendAsync(_h.Post("/api/logout", "{}"));
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual("Bearer " + Harness.Token, _h.Requests().Single(r => r.PathAndQuery == "/api/logout").Authorization);
        Assert.IsNull(_h.SessionCookie());
    }

    [TestMethod]
    public async Task WrongPasswordsAreCountedPerVisitor()
    {
        for (var i = 0; i < LoginThrottle.PerLogin; ++i)
        {
            using var wrong = await _h.Client.SendAsync(_h.Post("/api/login", """{"login":"Anna","password":"wrong"}"""));
            Assert.AreEqual(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }
        using var blocked = await _h.Client.SendAsync(_h.Post("/api/login", """{"login":"anna","password":"right"}"""));
        Assert.AreEqual((HttpStatusCode)429, blocked.StatusCode);
        Assert.IsTrue(blocked.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.AreEqual(LoginThrottle.PerLogin, _h.Requests().Count, "the blocked try never reached the server");

        // Another login of the same visitor still goes.
        using var other = await _h.Client.SendAsync(_h.Post("/api/login", """{"login":"boris","password":"right"}"""));
        Assert.AreEqual(HttpStatusCode.OK, other.StatusCode);
    }

    [TestMethod]
    public async Task RetryAfterOfABusyServerReachesThePage()
    {
        await _h.SignInAsync();
        using var answer = await _h.Client.SendAsync(_h.Post("/api/share/leave", """{"twinId":"t"}"""));
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
        Assert.AreEqual(TimeSpan.FromSeconds(7), answer.Headers.RetryAfter?.Delta);
    }

    [TestMethod]
    public async Task TheSiteItselfIsServedWithItsHeaders()
    {
        using var page = await _h.Client.GetAsync("/");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        StringAssert.Contains(await page.Content.ReadAsStringAsync(), "Космос");
        StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "default-src 'self'");
        Assert.AreEqual("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.AreEqual("no-cache", page.Headers.CacheControl?.ToString());

        using var script = await _h.Client.GetAsync("/js/app.js");
        Assert.AreEqual("text/javascript", script.Content.Headers.ContentType?.MediaType);

        using var missing = await _h.Client.GetAsync("/secret.txt");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task ConfigNamesTheServerForShareLinks()
    {
        using var answer = await _h.Client.GetAsync("/web/config");
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        Assert.AreEqual("cosmos-calendar-web", json.RootElement.GetProperty("service").GetString());
        // The stand-in listens on http://127.0.0.1:<port>, and a plain http
        // server is named the way the planner names its home server.
        StringAssert.Matches(json.RootElement.GetProperty("serverAddress").GetString(),
            new System.Text.RegularExpressions.Regex(@"^127\.0\.0\.1:\d+$"));
    }

    [TestMethod]
    public async Task HealthAsksTheServer()
    {
        using var answer = await _h.Client.GetAsync("/web/health");
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        StringAssert.Contains(await answer.Content.ReadAsStringAsync(), "calendar-planner-server");
    }
}

[TestClass]
public sealed class UpstreamTests
{
    [TestMethod]
    public async Task ANServerNobodyListensOnIsReportedNotHidden()
    {
        // A port taken and released at once: nobody listens there.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var h = await Harness.StartAsync($"127.0.0.1:{port}");
        using var answer = await h.Client.GetAsync("/api/ping");
        Assert.AreEqual(HttpStatusCode.BadGateway, answer.StatusCode);
        StringAssert.Contains(await answer.Content.ReadAsStringAsync(), "\"error\":\"upstream\"");

        using var health = await h.Client.GetAsync("/web/health");
        Assert.AreEqual(HttpStatusCode.BadGateway, health.StatusCode);
    }

    [TestMethod]
    public async Task ThePublicAddressGoesIntoTheConfig()
    {
        await using var h = await Harness.StartAsync(configure: o => o.PublicServerAddress = "https://cal.example.org");
        using var answer = await h.Client.GetAsync("/web/config");
        StringAssert.Contains(await answer.Content.ReadAsStringAsync(), "https://cal.example.org");
    }

    [TestMethod]
    public void UpstreamAddressesAreReadLikeThePlannerWritesThem()
    {
        Assert.AreEqual("http://127.0.0.1:8765/", new WebOptions { Upstream = "127.0.0.1:8765" }.UpstreamUri().ToString());
        Assert.AreEqual("https://cal.example.org/", new WebOptions { Upstream = "https://cal.example.org/" }.UpstreamUri().ToString());
        Assert.AreEqual("192.168.0.146:8765", new WebOptions { Upstream = "192.168.0.146:8765" }.ServerAddressForLinks());
        Assert.AreEqual("https://cal.example.org", new WebOptions { Upstream = "https://cal.example.org" }.ServerAddressForLinks());
    }

    [TestMethod]
    public void TheThrottleLetsGoAfterTheWindow()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var throttle = new LoginThrottle(() => now);
        var client = IPAddress.Parse("10.0.0.5");
        for (var i = 0; i < LoginThrottle.PerLogin; ++i)
            throttle.Fail(client, "anna");
        Assert.IsTrue(throttle.RetryAfterSeconds(client, "ANNA") > 0, "logins compare without case");
        Assert.AreEqual(0, throttle.RetryAfterSeconds(IPAddress.Parse("10.0.0.6"), "anna"), "another visitor is not blocked");

        now += LoginThrottle.Window;
        Assert.AreEqual(0, throttle.RetryAfterSeconds(client, "anna"));
    }

    [TestMethod]
    public void ManyLoginsFromOneVisitorAreLimitedToo()
    {
        var throttle = new LoginThrottle();
        var client = IPAddress.Parse("10.0.0.7");
        for (var i = 0; i < LoginThrottle.PerAddress; ++i)
            throttle.Fail(client, "user" + i);
        Assert.IsTrue(throttle.RetryAfterSeconds(client, "fresh") > 0);
    }

    [TestMethod]
    public void OnlyThePlannersRoutesAreInTheTable()
    {
        Assert.IsNotNull(ApiRoutes.Find("/api/sync/"));
        Assert.IsNull(ApiRoutes.Find("/api/sync/x"));
        Assert.IsNull(ApiRoutes.Find("/api/SYNC"));
        Assert.AreEqual(RouteKind.SignIn, ApiRoutes.Find("/api/login")!.Kind);
    }
}
