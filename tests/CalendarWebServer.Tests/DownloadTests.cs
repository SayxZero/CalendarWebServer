using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

/// <summary>The APK offered by the presentation page, from the data folder.</summary>
[TestClass]
public sealed class DownloadTests
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

    private async Task<JsonElement> AndroidAsync()
    {
        using var answer = await _h.Client.GetAsync("/web/downloads");
        Assert.AreEqual(HttpStatusCode.OK, answer.StatusCode);
        using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("android").Clone();
    }

    [TestMethod]
    public async Task WithoutAFileThereIsNothingToOffer()
    {
        Assert.AreEqual(JsonValueKind.Null, (await AndroidAsync()).ValueKind);
        using var file = await _h.Client.GetAsync("/" + AppDownloads.AndroidPath);
        Assert.AreEqual(HttpStatusCode.NotFound, file.StatusCode);
    }

    [TestMethod]
    public async Task TheNewestVersionWinsAsANumber()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "CosmosCalendar-1.9.0.apk"), "old");
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "CosmosCalendar-1.10.0.apk"), "new");
        var android = await AndroidAsync();
        Assert.AreEqual("1.10.0", android.GetProperty("version").GetString());
        Assert.AreEqual(AppDownloads.AndroidPath, android.GetProperty("url").GetString());

        using var file = await _h.Client.GetAsync("/" + AppDownloads.AndroidPath);
        Assert.AreEqual(HttpStatusCode.OK, file.StatusCode);
        Assert.AreEqual("new", await file.Content.ReadAsStringAsync());
        Assert.AreEqual(AppDownloads.ApkType, file.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("attachment", file.Content.Headers.ContentDisposition?.DispositionType);
    }

    [TestMethod]
    public async Task OtherFilesAreNotOffered()
    {
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "something.apk"), "x");
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "CosmosCalendar-1.1.0.apk.txt"), "x");
        Assert.AreEqual(JsonValueKind.Null, (await AndroidAsync()).ValueKind);
    }
}
