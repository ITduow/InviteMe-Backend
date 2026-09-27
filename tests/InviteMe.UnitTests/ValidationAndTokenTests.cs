using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Models;
using InviteMe.Infrastructure.Authentication;

namespace InviteMe.UnitTests;

public sealed class ValidationAndTokenTests
{
    [Theory]
    [InlineData(0, 25)]
    [InlineData(1, 101)]
    [InlineData(1, 0)]
    [InlineData(1_000_001, 1)]
    public async Task PaginationRejectsInvalidBounds(int page, int size)
    {
        var validation = new RequestValidation<PageRequest>([new PageRequestValidator()]);
        await Assert.ThrowsAsync<ValidationException>(() => validation.ValidateAsync(new(page, size), default));
    }

    [Fact]
    public async Task PaginationAcceptsMaximumPageSize()
    {
        await new RequestValidation<PageRequest>([new PageRequestValidator()]).ValidateAsync(new(1, 100), default);
        Assert.Equal(3, new PagedResult<int>([], 1, 100, 201).TotalPages);
    }

    [Fact]
    public void TokensAreHighEntropyAndHashMatchesKnownSha256Vector()
    {
        var first = InvitationToken.Generate();
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, InvitationToken.Generate());
        Assert.NotEqual(first, InvitationToken.Hash(first));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            InvitationToken.Hash("abc"));
        Assert.Throws<ArgumentException>(() => InvitationToken.Hash(" "));
    }
}
