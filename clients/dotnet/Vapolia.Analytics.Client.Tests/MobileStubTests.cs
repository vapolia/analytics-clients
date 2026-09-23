namespace Vapolia.Analytics.Client.Tests;

/// <summary>Plain net10.0 carries the app types as no-op stubs, so a test calling them never throws.</summary>
[TestClass]
public class MobileStubTests
{
    [TestMethod]
    public async Task AppTypesDoNothingOnPlainNet10()
    {
        var options = new AnalyticsOptions { IngestionUrl = new Uri("https://localhost/testsource") };

        Assert.AreSame(NullAnalytics.Instance, MobileAnalytics.Start(options));
        Assert.AreSame(NullAnalytics.Instance, MobileAnalytics.Current);
        Assert.IsNull(MobileAnalytics.Identity);
        MobileAnalytics.Lifecycle.Foreground += () => Assert.Fail("never raised");
        await MobileAnalytics.FlushAsync();
        await MobileAnalytics.StopAsync();

        var identity = new MobileInstallIdentityProvider(options);
        identity.IsOptedOut = true;
        Assert.IsTrue(identity.IsOptedOut);
        Assert.IsNull(identity.InstallId);
        Assert.IsNull(identity.ConsentAnswer);
        Assert.IsFalse(identity.IsFirstRun);
        Assert.IsFalse(identity.Seed(new InstallSeed("11111111-0000-0000-0000-000011111111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
    }
}
