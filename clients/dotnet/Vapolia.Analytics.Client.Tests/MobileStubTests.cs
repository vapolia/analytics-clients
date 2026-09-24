namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class MobileStubTests
{
    [TestMethod]
    public async Task AppTypesThrownsOnPlainNet10()
    {
        try
        {
            var options = new AnalyticsOptions { IngestionUrl = new Uri("https://localhost/testsource") };

            Assert.AreSame(NullAnalytics.Instance, MobileAnalytics.Start(options));
            Assert.AreSame(NullAnalytics.Instance, MobileAnalytics.Current);
            Assert.IsNull(MobileAnalytics.Identity);
            MobileAnalytics.Lifecycle.Foreground += () => Assert.Fail("never raised");
            await MobileAnalytics.FlushAsync();
            await MobileAnalytics.StopAsync();

            var identity = new MobileInstallContext(options)
            {
                IsOptedOut = true
            };
            Assert.IsTrue(identity.IsOptedOut);
            Assert.IsNull(identity.InstallId);
            Assert.IsNull(identity.ConsentAnswer);
            Assert.IsFalse(identity.IsFirstRun);
            Assert.IsFalse(identity.Seed(new InstallSeed("11111111-0000-0000-0000-000011111111", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));
        }
        catch (NotImplementedException)
        {
            //Succeeded
            return;
        }
        
        Assert.Fail("Should throw not implemented exceptions");
    }
}
