using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

/// <summary>
/// The site in front of a real CalendarServer. Runs only with
/// WEB_TEST_SERVER=&lt;address&gt; - a test server with its own port and data
/// folder, never the one with real accounts: it registers an account there.
/// </summary>
[TestClass]
public sealed class RealServerTests
{
    [TestMethod]
    public async Task ARecordGoesToTheServerAndComesBack()
    {
        var address = Environment.GetEnvironmentVariable("WEB_TEST_SERVER");
        if (string.IsNullOrWhiteSpace(address))
            Assert.Inconclusive("WEB_TEST_SERVER не задан: тест с настоящим сервером пропущен");

        await using var h = await Harness.StartAsync(address);
        var login = "webtest" + Guid.NewGuid().ToString("N")[..8];

        using (var register = await h.Client.SendAsync(h.Post("/api/register",
                   $$"""{"login":"{{login}}","password":"secret123","device":"tst_web"}""")))
        {
            Assert.AreEqual(HttpStatusCode.Created, register.StatusCode, await register.Content.ReadAsStringAsync());
            Assert.IsNotNull(h.SessionCookie());
        }

        var modified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var change = $$$"""{"since":0,"device":"tst_web","changes":[{"kind":"calendar","id":"web-cal","modified":{{{modified}}},"deleted":false,"data":{"id":"web-cal","name":"С сайта","color":"#0f9d58"}}]}""";
        using (var sync = await h.Client.SendAsync(h.Post("/api/sync", change)))
        {
            Assert.AreEqual(HttpStatusCode.OK, sync.StatusCode, await sync.Content.ReadAsStringAsync());
            using var json = JsonDocument.Parse(await sync.Content.ReadAsStringAsync());
            Assert.AreEqual(1, json.RootElement.GetProperty("accepted").GetInt32());
        }

        using (var record = await h.Client.GetAsync("/api/record?kind=calendar&id=web-cal"))
        {
            using var json = JsonDocument.Parse(await record.Content.ReadAsStringAsync());
            Assert.IsTrue(json.RootElement.GetProperty("found").GetBoolean());
            Assert.AreEqual("С сайта", json.RootElement.GetProperty("change").GetProperty("data")
                .GetProperty("name").GetString());
        }

        using (var logout = await h.Client.SendAsync(h.Post("/api/logout", "{}")))
            Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
        using (var after = await h.Client.GetAsync("/api/me"))
            Assert.AreEqual(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
