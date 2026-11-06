using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Vapolia.Analytics.Client.Tests;

[TestClass]
public class ClientAddressTests
{
    const string InstallId = "11111111-0000-0000-0000-000011111111";

    sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    static string? Header(HttpRequestMessage request)
        => request.Headers.TryGetValues("X-Analytics-Client-Address", out var values) ? values.Single() : null;

    [TestMethod]
    public void CleansAnAddress()
    {
        Assert.AreEqual("198.51.100.4", Clean.IpAddress("::FFFF:198.51.100.4"));
        Assert.AreEqual("2001:db8::1", Clean.IpAddress(" 2001:DB8::1%4 "));
        Assert.AreEqual("203.0.113.7", Clean.IpAddress("203.0.113.7"));
        Assert.IsNull(Clean.IpAddress("not-an-ip"));
        Assert.IsNull(Clean.IpAddress("1"));
        Assert.IsNull(Clean.IpAddress(null));
    }

    [TestMethod]
    public async Task SendsOneRequestPerAddress()
    {
        var collector = new FakeCollector();
        var options = new AnalyticsOptions
        {
            IngestionUrl = new Uri("https://localhost/testsource"),
            AdvancedOptions = { FlushInterval = TimeSpan.FromHours(1) },
        };
        await using var sender = new Sender(options, collector, null, NullLogger.Instance);
        var device = new Device { Country = "FR" };

        sender.Track(new Pending(new BatchKey(InstallId, device, "{}", "203.0.113.1"), new Event("a", DateTimeOffset.UtcNow, null)));
        sender.Track(new Pending(new BatchKey(InstallId, device, "{}", "203.0.113.2"), new Event("b", DateTimeOffset.UtcNow, null)));
        sender.Track(new Pending(new BatchKey(InstallId, device, "{}", "203.0.113.1"), new Event("c", DateTimeOffset.UtcNow, null)));
        await sender.FlushAsync();

        CollectionAssert.AreEquivalent(new[] { "203.0.113.1", "203.0.113.2" }, collector.Addresses.ToArray());
    }

    [TestMethod]
    public async Task SendsTheHeaderUnderATokenOnly()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        var url = new Uri("https://localhost/testsource");

        await new HttpPublishHelper(http, url, "server-token").PostAsync("{}", "203.0.113.7", CancellationToken.None);
        await new HttpPublishHelper(http, url).PostAsync("{}", "203.0.113.7", CancellationToken.None);
        await new HttpPublishHelper(http, url, "server-token").PostAsync("{}", null, CancellationToken.None);

        Assert.AreEqual("203.0.113.7", Header(handler.Requests[0]));
        Assert.IsNull(Header(handler.Requests[1]));
        Assert.IsNull(Header(handler.Requests[2]));
    }

    [TestMethod]
    public void NeverWritesTheAddressToTheSpool()
    {
        var path = Path.Combine(Path.GetTempPath(), $"spool-{Guid.NewGuid():N}.json");
        try
        {
            var spool = new PersistPendingItemsToLocalStorageHelper(path, 10, null);
            spool.Save([new Pending(new BatchKey(InstallId, new Device { Country = "FR" }, "{}", "203.0.113.7"), new Event("a", DateTimeOffset.UtcNow, null))]);

            Assert.IsFalse(File.ReadAllText(path).Contains("203.0.113.7"));
            Assert.IsNull(spool.Load().Single().Key.ClientAddress);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void ReadsTheVisitorAddressFromTheConnection()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.9");
        var provider = new CookieInstallIdentityProvider(
            new HttpContextAccessor { HttpContext = context },
            Options.Create(new AnalyticsOptions { IngestionUrl = new Uri("https://localhost/testsource") }));

        Assert.AreEqual("203.0.113.9", Clean.IpAddress(((IInstallContext)provider).ClientAddress));
    }
}
