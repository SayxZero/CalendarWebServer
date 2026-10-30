using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CosmosCalendar.Web.Tests;

/// <summary>The prices of the levels of access, from the data folder.</summary>
[TestClass]
public sealed class PricingTests
{
    private string _dataDir = "";
    private Harness _h = null!;

    [TestInitialize]
    public async Task Start()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "cosmos-pricing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        _h = await Harness.StartAsync(configure: options => options.DataDir = _dataDir);
    }

    [TestCleanup]
    public async Task Stop()
    {
        await _h.DisposeAsync();
        Directory.Delete(_dataDir, true);
    }

    private async Task<JsonElement> PricingAsync()
    {
        using var answer = await _h.Client.GetAsync("/web/pricing");
        answer.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private Task WritePricingAsync(string json) =>
        File.WriteAllTextAsync(Path.Combine(_dataDir, PricingInfo.FileName), json, new UTF8Encoding(false));

    [TestMethod]
    public async Task NoFileMeansNoPrices()
    {
        var pricing = await PricingAsync();
        foreach (var name in new[] { "currency", "month", "year" })
            Assert.AreEqual("", pricing.GetProperty(name).GetString(), name);
    }

    [TestMethod]
    public async Task PricesComeFromTheFileWithoutARestart()
    {
        await WritePricingAsync("""{"currency":"₽","month":"199","year":1990}""");
        var pricing = await PricingAsync();
        Assert.AreEqual("₽", pricing.GetProperty("currency").GetString());
        Assert.AreEqual("199", pricing.GetProperty("month").GetString());
        Assert.AreEqual("1990", pricing.GetProperty("year").GetString(), "a number without quotes is a price too");

        await WritePricingAsync("""{"month":"249"}""");
        Assert.AreEqual("249", (await PricingAsync()).GetProperty("month").GetString());
    }

    [TestMethod]
    public async Task BrokenFileMeansNoPrices()
    {
        await WritePricingAsync("""{"month": "1""");
        Assert.AreEqual("", (await PricingAsync()).GetProperty("month").GetString());
    }
}
