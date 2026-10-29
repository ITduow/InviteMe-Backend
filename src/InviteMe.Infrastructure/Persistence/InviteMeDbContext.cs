using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Billing;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Notifications;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Identity;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace InviteMe.Infrastructure.Persistence;

public sealed class InviteMeDbContext(DbContextOptions<InviteMeDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    public DbSet<Wedding> Weddings => Set<Wedding>();
    public DbSet<WeddingMember> WeddingMembers => Set<WeddingMember>();
    public DbSet<WeddingMemberPermission> WeddingMemberPermissions => Set<WeddingMemberPermission>();
    public DbSet<WeddingMemberGroupPermission> WeddingMemberGroupPermissions => Set<WeddingMemberGroupPermission>();
    public DbSet<CoHostInvitation> CoHostInvitations => Set<CoHostInvitation>();
    public DbSet<CoHostInvitationGrant> CoHostInvitationGrants => Set<CoHostInvitationGrant>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<InvitationTemplate> Templates => Set<InvitationTemplate>();
    public DbSet<StoryTemplate> StoryTemplates => Set<StoryTemplate>();
    public DbSet<WeddingSettings> WeddingSettings => Set<WeddingSettings>();
    public DbSet<InviteMe.Infrastructure.Persistence.Adapters.StoryWishRow> StoryWishes => Set<InviteMe.Infrastructure.Persistence.Adapters.StoryWishRow>();
    public DbSet<InvitationDraft> InvitationDrafts => Set<InvitationDraft>();
    public DbSet<InvitationSendBatch> InvitationSendBatches => Set<InvitationSendBatch>();
    public DbSet<InvitationSendJob> InvitationSendJobs => Set<InvitationSendJob>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<InviteMe.Infrastructure.Notifications.AccountConfirmationEmailJob> AccountConfirmationEmailJobs => Set<InviteMe.Infrastructure.Notifications.AccountConfirmationEmailJob>();
    public DbSet<ThankYouMessage> ThankYouMessages => Set<ThankYouMessage>();
    public DbSet<ThankYouSendBatch> ThankYouSendBatches => Set<ThankYouSendBatch>();
    public DbSet<ThankYouSendJob> ThankYouSendJobs => Set<ThankYouSendJob>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // The SQL schema owns indexes; do not invent indexes for every mapped FK.
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("inviteme");
        builder.ApplyConfigurationsFromAssembly(typeof(InviteMeDbContext).Assembly);
    }
}
