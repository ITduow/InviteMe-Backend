using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.UnitTests;

public sealed class WeddingCoverUploadHandlerTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid WeddingId = Guid.NewGuid();

    [Fact]
    public async Task DatabaseFailureCleansUpNewlyUploadedFile()
    {
        var store = new FakeStore { SetCoverError = new InvalidOperationException("simulated persistence failure") };
        var media = new FakeMedia();
        var handler = Handler(store, media);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.UploadCoverImageAsync(WeddingId,
            new MemoryStream([0xFF, 0xD8, 0xFF]), 3, 1, 5 * 1024 * 1024, ["jpeg"], default));
        Assert.Equal("random.jpg", media.DeletedKey);
    }

    [Fact]
    public async Task StorageFailureDoesNotReachWeddingPersistence()
    {
        var store = new FakeStore();
        var media = new FakeMedia { SaveError = new IOException("simulated storage failure") };
        var handler = Handler(store, media);
        await Assert.ThrowsAsync<IOException>(() => handler.UploadCoverImageAsync(WeddingId,
            new MemoryStream([0xFF, 0xD8, 0xFF]), 3, 1, 5 * 1024 * 1024, ["jpeg"], default));
        Assert.Equal(0, store.SetCoverCalls);
    }

    [Fact]
    public async Task InvalidContentIsRejectedBeforeStorage()
    {
        var store = new FakeStore();
        var media = new FakeMedia();
        var handler = Handler(store, media);
        var error = await Assert.ThrowsAsync<ApplicationProblemException>(() => handler.UploadCoverImageAsync(WeddingId,
            new MemoryStream("<svg/>"u8.ToArray()), 6, 1, 5 * 1024 * 1024, ["jpeg", "png", "webp"], default));
        Assert.Equal("WEDDING_COVER_UNSUPPORTED", error.Code);
        Assert.Equal(0, media.SaveCalls);
    }

    private static WeddingSetupHandler Handler(FakeStore store, FakeMedia media) => new(store, media,
        new FakePermissions(), new FakeAccess(), new FakeCurrentUser(),
        new RequestValidation<CreateWeddingInput>([]), new RequestValidation<UpdateWeddingInput>([]), new RequestValidation<string>([]));

    private sealed class FakeCurrentUser : ICurrentUser { public Guid? UserId => OwnerId; }
    private sealed class FakeAccess : IWeddingAccessReader
    {
        public Task<WeddingAccess?> FindAsync(Guid weddingId, Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<WeddingAccess?>(new(OwnerId, false, new HashSet<WeddingPermission>()));
    }
    private sealed class FakePermissions : IWeddingPermissionService
    {
        public Task RequireAsync(Guid weddingId, WeddingPermission permission, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> HasPermissionAsync(Guid weddingId, WeddingPermission permission, CancellationToken cancellationToken) => Task.FromResult(true);
    }
    private sealed class FakeStore : IWeddingSetupStore
    {
        public Exception? SetCoverError { get; init; }
        public int SetCoverCalls { get; private set; }
        public Task<WeddingSetupDto> CreateAsync(Guid ownerId, CreateWeddingInput input, string normalizedSlug, CancellationToken ct) => throw new NotImplementedException();
        public Task<WeddingSetupDto?> GetAsync(Guid weddingId, CancellationToken ct) => throw new NotImplementedException();
        public Task<WeddingSetupDto> UpdateAsync(Guid weddingId, UpdateWeddingInput input, string normalizedSlug, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> SlugAvailableAsync(string normalizedSlug, Guid? exceptWeddingId, CancellationToken ct) => throw new NotImplementedException();
        public Task<WeddingCoverUpdateResult> SetCoverImageAsync(Guid weddingId, string imageUrl, int expectedVersion, Guid actorId, CancellationToken ct)
        {
            SetCoverCalls++;
            if (SetCoverError is not null) throw SetCoverError;
            return Task.FromResult(new WeddingCoverUpdateResult(null, expectedVersion + 1));
        }
        public Task<WeddingCoverUpdateResult> RemoveCoverImageAsync(Guid weddingId, int expectedVersion, Guid actorId, CancellationToken ct) => throw new NotImplementedException();
    }
    private sealed class FakeMedia : IWeddingMediaStorage
    {
        public Exception? SaveError { get; init; }
        public int SaveCalls { get; private set; }
        public string? DeletedKey { get; private set; }
        public Task<StoredWeddingImage> SaveAsync(Guid weddingId, Stream content, string extension, CancellationToken ct)
        {
            SaveCalls++;
            if (SaveError is not null) throw SaveError;
            return Task.FromResult(new StoredWeddingImage("random.jpg", $"/media/wedding-covers/{weddingId:N}/random.jpg"));
        }
        public Task<Stream?> OpenReadAsync(Guid weddingId, string fileName, CancellationToken ct) => Task.FromResult<Stream?>(null);
        public Task DeleteAsync(Guid weddingId, string fileName, CancellationToken ct) { DeletedKey = fileName; return Task.CompletedTask; }
        public bool TryGetOwnedFileName(Guid weddingId, string? reference, out string fileName) { fileName = ""; return false; }
    }
}
