using System.Net;
using System.Text;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Gifts;
using InviteMe.Application.Ports.Gifts;
using InviteMe.Domain.Gifts;
using InviteMe.Infrastructure.Gifts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace InviteMe.UnitTests;

public sealed class BankDirectoryTests
{
    private const string Good = """{"code":"00","data":[{"bin":"970431","shortName":"Eximbank","code":"EIB","logo":"https://api.vietqr.io/img/EIB.png"}]}""";
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Store : IBankDirectoryCacheStore
    {
        public BankDirectoryCache? Row;
        public int Writes;
        public Task<BankDirectoryCache?> ReadAsync(CancellationToken ct) => Task.FromResult(Row);
        public Task WriteAsync(BankDirectoryCache entry, CancellationToken ct) { Row = entry; Writes++; return Task.CompletedTask; }
    }
    private sealed class Source : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Body = Good;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.vietqr.io/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://api.vietqr.io/v2/banks", request.RequestUri!.ToString());
            Interlocked.Increment(ref Requests);
            await Task.Delay(10, ct);
            return new(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }
    private static VietQrBankDirectory Service(Source source, Store store, MemoryCache memory, Clock clock) =>
        new(source, store, memory, clock, NullLogger<VietQrBankDirectory>.Instance);

    [Fact]
    public async Task ConcurrentColdRequestsFetchOnceAndPersistNewBankOutsideLegacyList()
    {
        using var source = new Source(); var store = new Store(); var clock = new Clock();
        using var memory = new MemoryCache(new MemoryCacheOptions()); using var service = Service(source, store, memory, clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.GetAsync(default)));
        Assert.Equal(1, source.Requests); Assert.Equal(1, store.Writes);
        Assert.All(results, banks => Assert.Equal("970431", Assert.Single(banks).Bin));
        Assert.Null(GiftBanks.Find("970431"));
        Assert.Equal(clock.Now, store.Row!.FetchedAt);
        Assert.Equal("VIETQR_BANKS", store.Row.Key);
    }

    [Fact]
    public async Task FreshDatabaseCacheSurvivesServiceRestartWithoutHttpCall()
    {
        using var source = new Source(); var store = new Store(); var clock = new Clock();
        using (var memory = new MemoryCache(new MemoryCacheOptions()))
        using (var service = Service(source, store, memory, clock)) await service.GetAsync(default);
        source.Status = HttpStatusCode.ServiceUnavailable;
        using var restartedMemory = new MemoryCache(new MemoryCacheOptions()); using var restarted = Service(source, store, restartedMemory, clock);
        Assert.Equal("Eximbank", Assert.Single(await restarted.GetAsync(default)).Name);
        Assert.Equal(1, source.Requests); Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task RefreshAt24HoursRetainsStaleSnapshotOnFailureAndRetriesAfterFiveMinutes()
    {
        using var source = new Source(); var store = new Store(); var clock = new Clock();
        using var memory = new MemoryCache(new MemoryCacheOptions()); using var service = Service(source, store, memory, clock);
        await service.GetAsync(default); var original = store.Row;
        clock.Now = clock.Now.AddHours(24).AddTicks(-1); await service.GetAsync(default); Assert.Equal(1, source.Requests);
        clock.Now = clock.Now.AddTicks(1); source.Status = HttpStatusCode.ServiceUnavailable;
        Assert.Equal("Eximbank", Assert.Single(await service.GetAsync(default)).Name);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetAsync(default)));
        Assert.Equal(2, source.Requests); Assert.Same(original, store.Row); Assert.Equal(1, store.Writes);
        clock.Now = clock.Now.AddMinutes(5); source.Status = HttpStatusCode.OK;
        await service.GetAsync(default);
        Assert.Equal(3, source.Requests); Assert.Equal(2, store.Writes); Assert.Equal(clock.Now, store.Row!.FetchedAt);
    }

    [Theory]
    [InlineData("{\"code\":\"00\",\"data\":[]}")]
    [InlineData("{\"code\":\"99\",\"data\":[]}")]
    [InlineData("not json")]
    [InlineData("{\"code\":\"00\",\"data\":[{\"bin\":\"invalid\",\"shortName\":\"Bank\"}]}")]
    [InlineData("{\"code\":\"00\",\"data\":[{\"bin\":\"970431\",\"shortName\":\"One\"},{\"bin\":\"970431\",\"shortName\":\"Two\"}]}")]
    public async Task InvalidResponseNeverOverwritesSuccessfulDatabaseSnapshot(string response)
    {
        using var source = new Source(); var store = new Store(); var clock = new Clock();
        using var memory = new MemoryCache(new MemoryCacheOptions()); using var service = Service(source, store, memory, clock);
        await service.GetAsync(default); var original = store.Row;
        clock.Now = clock.Now.AddDays(2); source.Body = response;
        Assert.Single(await service.GetAsync(default)); Assert.Same(original, store.Row); Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task InitialFailureWithoutCacheReturnsUnavailableAndDoesNotHammerProvider()
    {
        using var source = new Source { Status = HttpStatusCode.ServiceUnavailable }; var store = new Store(); var clock = new Clock();
        using var memory = new MemoryCache(new MemoryCacheOptions()); using var service = Service(source, store, memory, clock);
        for (var i = 0; i < 3; i++)
        {
            var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => service.GetAsync(default));
            Assert.Equal("BANK_DIRECTORY_UNAVAILABLE", error.Code);
        }
        Assert.Equal(1, source.Requests); Assert.Null(store.Row);
    }
}
