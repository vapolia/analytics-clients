using Vapolia.Analytics.Client;

namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class CleanTests
{
    [TestMethod]
    public void TextTrimsCapsAndStripsControlCharacters()
    {
        Assert.AreEqual("hello", Clean.Text("  hello  ", 64));
        Assert.AreEqual("ab", Clean.Text("a\0b", 64));
        Assert.AreEqual(64, Clean.Text(new string('x', 100), 64)!.Length);
        Assert.IsNull(Clean.Text("   ", 64));
        Assert.IsNull(Clean.Text(null, 64));
    }

    [TestMethod]
    public void CountryIsTwoAsciiLettersOrNothing()
    {
        Assert.AreEqual("FR", Clean.Country("fr"));
        Assert.AreEqual("FR", Clean.Country(" FR "));
        // "GERMANY" truncated would be Georgia: unknown beats wrong.
        Assert.IsNull(Clean.Country("GERMANY"));
        Assert.IsNull(Clean.Country("F"));
        Assert.IsNull(Clean.Country("F1"));
        Assert.IsNull(Clean.Country(null));
    }

    [TestMethod]
    public void InstallIdIsACanonicalNonNilGuid()
    {
        Assert.AreEqual(
            "11111111-0000-0000-0000-000011111111",
            Clean.InstallId("11111111-0000-0000-0000-000011111111"));
        Assert.AreEqual(
            "aabbccdd-0000-0000-0000-00000000000f",
            Clean.InstallId("AABBCCDD-0000-0000-0000-00000000000F"));
        Assert.IsNull(Clean.InstallId("00000000-0000-0000-0000-000000000000"));
        Assert.IsNull(Clean.InstallId("not-a-guid"));
        // Braces and the dash-less form are not what the collector's clients send.
        Assert.IsNull(Clean.InstallId("{11111111-0000-0000-0000-000011111111}"));
        Assert.IsNull(Clean.InstallId(null));
    }

    [TestMethod]
    public void PropsKeepCappedScalarsOnly()
    {
        var kept = Clean.Props(new Dictionary<string, object?>
        {
            ["mode"] = new string('x', Clean.MaxValueLength + 10),
            ["ok"] = true,
            ["moves"] = 34,
            ["ratio"] = 0.5,
            ["nan"] = double.NaN,
            ["infinite"] = double.PositiveInfinity,
            ["blank"] = "   ",
            ["nothing"] = null,
            ["nested"] = new Dictionary<string, object>(),
            ["list"] = new[] { 1, 2 },
        })!;

        Assert.AreEqual(PropValue.From(true), kept["ok"]);
        Assert.AreEqual(PropValue.From(34d), kept["moves"]);
        Assert.AreEqual(PropValue.From(0.5d), kept["ratio"]);
        Assert.AreEqual(Clean.MaxValueLength, kept["mode"].StringValue!.Length);
        Assert.IsFalse(kept.ContainsKey("nan"));
        Assert.IsFalse(kept.ContainsKey("infinite"));
        Assert.IsFalse(kept.ContainsKey("blank"));
        Assert.IsFalse(kept.ContainsKey("nothing"));
        Assert.IsFalse(kept.ContainsKey("nested"));
        Assert.IsFalse(kept.ContainsKey("list"));
    }

    [TestMethod]
    public void PropsAreCappedInCountInKeyOrder()
    {
        var props = new Dictionary<string, object?>();
        for (var index = 0; index <= 20; index++)
            props[$"p{index:D2}"] = index;

        var kept = Clean.Props(props)!;

        Assert.AreEqual(Clean.MaxPropsPerEvent, kept.Count);
        Assert.IsTrue(kept.ContainsKey("p00"));
        Assert.IsFalse(kept.ContainsKey("p12"));
    }

    [TestMethod]
    public void DeviceNormalizesAndRefusesExcludedCountries()
    {
        var clean = new Device { Country = "fr" }.Cleaned()!;

        Assert.AreEqual("FR", clean.Country);

        Assert.IsNull(new Device { Country = "KR" }.Cleaned(["KR"]));
        Assert.IsNull(new Device { Country = "kr" }.Cleaned(["KR"]));
        Assert.AreEqual("KR", new Device { Country = "KR" }.Cleaned()!.Country, "no country is excluded unless the app lists it");
    }
}
