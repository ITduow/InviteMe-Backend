using InviteMe.Infrastructure.Authorization;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.UnitTests;

public sealed class PersistenceModelTests
{
    [Fact]
    public void IdentityAndWeddingModelMatchExpectedSchema()
    {
        var options = new DbContextOptionsBuilder<InviteMeDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=unused;Password=unused").Options;
        using var context = new InviteMeDbContext(options);
        var user = context.Model.FindEntityType(typeof(ApplicationUser))!;
        Assert.Equal("inviteme", user.GetSchema());
        Assert.Equal("users", user.GetTableName());
        Assert.Equal(typeof(Guid), user.FindPrimaryKey()!.Properties.Single().ClrType);
        Assert.Equal("email", user.FindProperty(nameof(ApplicationUser.Email))!.GetColumnName());
        Assert.Equal("password_hash", user.FindProperty(nameof(ApplicationUser.PasswordHash))!.GetColumnName());
        Assert.Equal("full_name", user.FindProperty(nameof(ApplicationUser.FullName))!.GetColumnName());
        Assert.Equal("weddings", context.Model.FindEntityType(typeof(InviteMe.Domain.Weddings.Wedding))!.GetTableName());
        Assert.All(context.Model.GetEntityTypes(), entity => Assert.Equal("inviteme", entity.GetSchema()));
    }

    [Fact]
    public void WeddingAccessQueryTranslatesAndScopesUserWeddingAndPermission()
    {
        var options = new DbContextOptionsBuilder<InviteMeDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=unused;Password=unused").Options;
        using var context = new InviteMeDbContext(options);

        var sql = new WeddingAccessReader(context).BuildQuery(Guid.NewGuid(), Guid.NewGuid()).ToQueryString();

        Assert.Contains("weddings", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wedding_members", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("wedding_member_permissions", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permissions", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ACTIVE", sql, StringComparison.Ordinal);
        Assert.Contains("WEDDING", sql, StringComparison.Ordinal);
    }
}
