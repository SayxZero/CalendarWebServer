using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

/// <summary>The branch site-only: the presentation page, and no way to anything else.</summary>
[TestClass]
public sealed class SiteOnlyTests
{
    private Harness _h = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public async Task Start()
    {
        _h = await Harness.StartAsync(configure: options =>
        {
            options.AllFeaturesForTests = false;
            // Set on purpose: even with all of it configured the mode stays closed.
            options.PublicUrl = "https://calendar.example";
            options.McpUpstream = "127.0.0.1:1";
            options.McpSecret = "a-long-secret-of-the-test-0123456789";
        });
        foreach (var file in new[] { "css/landing.css", "js/landing.js", "js/landingPrices.js", "img/logo.svg", "video/hero.mp4", "docs/mcp-web.md", "js/main.js", "js/core/store.js", "app/index.html", "connect/index.html" })
        {
            var path = Path.Combine(_h.WebRoot, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, file);
        }
        _client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = _h.SiteUri };
    }

    [TestCleanup]
    public async Task Stop()
    {
        _client.Dispose();
        await _h.DisposeAsync();
    }

    [DataTestMethod]
    [DataRow("/")]
    [DataRow("/index.html")]
    [DataRow("/css/landing.css")]
    [DataRow("/js/landing.js")]
    [DataRow("/js/landingPrices.js")]
    [DataRow("/img/logo.svg")]
    [DataRow("/video/hero.mp4")]
    [DataRow("/docs/mcp-web.md")]
    [DataRow("/web/pricing")]
    public async Task ThePageAndWhatItNeedsAreThere(string path)
    {
        using var answer = await _client.GetAsync(path);
        Assert.AreNotEqual(HttpStatusCode.NotFound, answer.StatusCode, path);
    }

    [DataTestMethod]
    [DataRow("/api/ping")]
    [DataRow("/api/me")]
    [DataRow("/api/sessions")]
    [DataRow("/web/config")]
    [DataRow("/web/downloads")]
    [DataRow("/web/download/android")]
    [DataRow("/mcp")]
    [DataRow("/oauth/authorize")]
    [DataRow("/oauth/request")]
    [DataRow("/.well-known/oauth-authorization-server")]
    [DataRow("/.well-known/oauth-protected-resource/mcp")]
    [DataRow("/connect/")]
    [DataRow("/connect/index.html")]
    [DataRow("/js/main.js")]
    [DataRow("/js/core/store.js")]
    [DataRow("/img/../js/main.js")]
    [DataRow("/js/%6dain.js")]
    [DataRow("/nothing")]
    public async Task EverythingElseIsNotThere(string path)
    {
        using var answer = await _client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, answer.StatusCode, path);
    }

    [DataTestMethod]
    [DataRow("/api/login")]
    [DataRow("/api/register")]
    [DataRow("/api/sync")]
    [DataRow("/oauth/register")]
    [DataRow("/oauth/token")]
    [DataRow("/mcp")]
    [DataRow("/")]
    public async Task NothingIsAcceptedFromOutside(string path)
    {
        using var answer = await _client.SendAsync(_h.Post(path, """{"login":"anna","password":"right"}"""));
        Assert.AreEqual(HttpStatusCode.NotFound, answer.StatusCode, path);
    }

    [TestMethod]
    public async Task NoRequestEverReachesASyncServer()
    {
        foreach (var path in new[] { "/", "/app/", "/api/ping", "/web/health", "/web/legal", "/web/pricing", "/mcp" })
        {
            using var _ = await _client.GetAsync(path);
        }
        using var login = await _client.SendAsync(_h.Post("/api/login", """{"login":"anna","password":"right"}"""));
        Assert.AreEqual(0, _h.Requests().Count);
    }

    [TestMethod]
    public async Task TheButtonsOfThePageFindAStubAndNotThePlanner()
    {
        foreach (var path in new[] { "/app", "/app/", "/app/index.html" })
        {
            using var answer = await _client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode, path);
            var text = await answer.Content.ReadAsStringAsync();
            StringAssert.Contains(text, "Планер в браузере скоро заработает");
            Assert.IsFalse(text.Contains("app/index.html"), "the file of the planner is not served");
        }
    }

    [TestMethod]
    public async Task HealthSpeaksForThisServerAlone()
    {
        using var answer = await _client.GetAsync("/web/health");
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        StringAssert.Contains(await answer.Content.ReadAsStringAsync(), "site-only");
    }

    [TestMethod]
    public async Task TheHeadersOfTheSiteStay()
    {
        using var answer = await _client.GetAsync("/api/ping");
        Assert.IsTrue(answer.Headers.Contains("X-Frame-Options"));
    }
}
