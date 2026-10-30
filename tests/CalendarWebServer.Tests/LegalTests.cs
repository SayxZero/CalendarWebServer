using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

/// <summary>The legal details of the presentation page and the offer, from the data folder.</summary>
[TestClass]
public sealed class LegalTests
{
    private Harness _h = null!;
    private string _dataDir = null!;

    [TestInitialize]
    public async Task Start()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cosmos-web-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _h = await Harness.StartAsync(configure: options => options.DataDir = _dataDir);
    }

    [TestCleanup]
    public async Task Stop()
    {
        await _h.DisposeAsync();
        Directory.Delete(_dataDir, true);
    }

    private async Task<JsonElement> LegalAsync()
    {
        using var answer = await _h.Client.GetAsync("/web/legal");
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual("no-cache", answer.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        return json.RootElement.Clone();
    }

    private Task WriteLegalAsync(string json) =>
        File.WriteAllTextAsync(Path.Combine(_dataDir, LegalInfo.FileName), json, new UTF8Encoding(false));

    [TestMethod]
    public async Task WithoutTheFileEveryFieldIsEmpty()
    {
        var legal = await LegalAsync();
        foreach (var name in new[] { "organization", "inn", "ogrnip", "phone", "email", "offerUrl", "offerTitle" })
            Assert.AreEqual("", legal.GetProperty(name).GetString(), name);
    }

    [TestMethod]
    public async Task TheOperatorsFileIsReadOnEveryRequest()
    {
        await WriteLegalAsync("""{"organization":"ИП Иванов","inn":"770000000000","phone":"+7 900 000-00-00","email":"a@b.ru"}""");
        var legal = await LegalAsync();
        Assert.AreEqual("ИП Иванов", legal.GetProperty("organization").GetString());
        Assert.AreEqual("770000000000", legal.GetProperty("inn").GetString());
        Assert.AreEqual("+7 900 000-00-00", legal.GetProperty("phone").GetString());
        Assert.AreEqual("", legal.GetProperty("ogrnip").GetString());

        await WriteLegalAsync("""{"organization":"ИП Петров"}""");
        Assert.AreEqual("ИП Петров", (await LegalAsync()).GetProperty("organization").GetString(), "no restart needed");
    }

    [TestMethod]
    public async Task AHandWrittenFileIsForgiven()
    {
        // Comments, a trailing comma, a number without quotes, a name in another case.
        await WriteLegalAsync("""
            {
              // ИП
              "Organization": "ИП Иванов",
              "inn": 770000000000,
              "ogrnip": "300000000000000",
            }
            """);
        var legal = await LegalAsync();
        Assert.AreEqual("ИП Иванов", legal.GetProperty("organization").GetString());
        Assert.AreEqual("770000000000", legal.GetProperty("inn").GetString());
        Assert.AreEqual("300000000000000", legal.GetProperty("ogrnip").GetString());
    }

    [TestMethod]
    public async Task ABrokenFileLeavesThePageWithoutDetails()
    {
        await WriteLegalAsync("""{"organization": "ИП""");
        Assert.AreEqual("", (await LegalAsync()).GetProperty("organization").GetString());
    }

    [TestMethod]
    public async Task TheOfferIsServedFromTheDataFolder()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n%offer\n");
        await File.WriteAllBytesAsync(Path.Combine(_dataDir, "Оферта.pdf"), pdf);
        await WriteLegalAsync("""{"offerFile":"Оферта.pdf","offerTitle":"Публичная оферта"}""");

        var legal = await LegalAsync();
        Assert.AreEqual(LegalInfo.OfferPath, legal.GetProperty("offerUrl").GetString());
        Assert.AreEqual("Публичная оферта", legal.GetProperty("offerTitle").GetString());

        using var answer = await _h.Client.GetAsync("/" + LegalInfo.OfferPath);
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        Assert.AreEqual("application/pdf", answer.Content.Headers.ContentType?.MediaType);
        CollectionAssert.AreEqual(pdf, await answer.Content.ReadAsByteArrayAsync());
        Assert.AreEqual("inline", answer.Content.Headers.ContentDisposition?.DispositionType);
        Assert.AreEqual("Оферта.pdf", answer.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.AreEqual(LegalInfo.OfferPolicy, string.Join("", answer.Headers.GetValues("Content-Security-Policy")));
    }

    [TestMethod]
    public async Task AnOfferThatIsNotThereIsNotLinked()
    {
        await WriteLegalAsync("""{"offerFile":"offer.pdf"}""");
        Assert.AreEqual("", (await LegalAsync()).GetProperty("offerUrl").GetString());
        using var answer = await _h.Client.GetAsync("/" + LegalInfo.OfferPath);
        Assert.AreEqual(HttpStatusCode.NotFound, answer.StatusCode);
    }

    [TestMethod]
    public async Task OnlyAFileRightInTheDataFolderIsHandedOut()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_dataDir)!, "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            foreach (var name in new[] { "../" + Path.GetFileName(outside), outside.Replace("\\", "\\\\"), LegalInfo.FileName })
            {
                await WriteLegalAsync("{\"offerFile\":\"" + name + "\"}");
                Assert.AreEqual("", (await LegalAsync()).GetProperty("offerUrl").GetString(), name);
                using var answer = await _h.Client.GetAsync("/" + LegalInfo.OfferPath);
                Assert.AreEqual(HttpStatusCode.NotFound, answer.StatusCode, name);
            }
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
