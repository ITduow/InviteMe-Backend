using InviteMe.Domain.AI;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Billing;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Notifications;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Identity;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence.ReadModels;
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
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<WeddingHeadcount> WeddingHeadcounts => Set<WeddingHeadcount>();
    public DbSet<TableOccupancy> TableOccupancies => Set<TableOccupancy>();
    public DbSet<GuestRsvpSummary> GuestRsvpSummaries => Set<GuestRsvpSummary>();

    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<InvitationTemplate> Templates => Set<InvitationTemplate>();
    public DbSet<TemplateSection> TemplateSections => Set<TemplateSection>();
    public DbSet<WeddingSettings> WeddingSettings => Set<WeddingSettings>();
    public DbSet<LoveStory> LoveStories => Set<LoveStory>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<WeddingEvent> WeddingEvents => Set<WeddingEvent>();
    public DbSet<WeddingMedia> WeddingMedia => Set<WeddingMedia>();
    public DbSet<GuestGroup> GuestGroups => Set<GuestGroup>();
    public DbSet<WeddingGuest> WeddingGuests => Set<WeddingGuest>();
    public DbSet<GuestParticipant> GuestParticipants => Set<GuestParticipant>();
    public DbSet<GuestNote> GuestNotes => Set<GuestNote>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<InvitationDelivery> InvitationDeliveries => Set<InvitationDelivery>();
    public DbSet<Rsvp> Rsvps => Set<Rsvp>();
    public DbSet<RsvpHistory> RsvpHistory => Set<RsvpHistory>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<ReceptionTable> Tables => Set<ReceptionTable>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<SeatingAssignment> SeatingAssignments => Set<SeatingAssignment>();
    public DbSet<SeatingChangeLog> SeatingChangeLogs => Set<SeatingChangeLog>();
    public DbSet<TableStatusHistory> TableStatusHistory => Set<TableStatusHistory>();
    public DbSet<WalkIn> WalkIns => Set<WalkIn>();
    public DbSet<CheckInRecord> CheckIns => Set<CheckInRecord>();
    public DbSet<Gift> Gifts => Set<Gift>();
    public DbSet<GiftMessage> GiftMessages => Set<GiftMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AiGeneration> AiGenerations => Set<AiGeneration>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // The SQL schema owns indexes; do not invent indexes for every mapped FK.
        configurationBuilder.Conventions.Remove(typeof(ForeignKeyIndexConvention));
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AdvanceSeatingVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AdvanceSeatingVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AdvanceSeatingVersions()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State == EntityState.Modified &&
                     (e.Entity is ReceptionTable || e.Entity is SeatingAssignment)))
        {
            var version = entry.Property("Version");
            version.CurrentValue = checked((int)version.OriginalValue! + 1);
        }
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasDefaultSchema("inviteme");
        builder.ApplyConfigurationsFromAssembly(typeof(InviteMeDbContext).Assembly);
    }
}
