namespace InviteMe.Domain.Seating;

public static class TableKinds
{
    public const string Primary = "PRIMARY";
    public const string Backup = "BACKUP";

    public static bool IsKnown(string? kind) => kind is Primary or Backup;
}

public static class TableStatuses
{
    public const string Planned = "PLANNED";
    public const string Active = "ACTIVE";
    public const string Backup = "BACKUP";
    public const string Inactive = "INACTIVE";
}

// Why a backup table had to be opened; required when a BACKUP table becomes ACTIVE.
public static class OverflowReasons
{
    public const string ExtraCompanions = "EXTRA_COMPANIONS";
    public const string WaitlistPromotion = "WAITLIST_PROMOTION";
    public const string LateRsvpChange = "LATE_RSVP_CHANGE";
    public const string WalkIn = "WALK_IN";
    public const string PlanningShortfall = "PLANNING_SHORTFALL";
    public const string Other = "OTHER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        ExtraCompanions, WaitlistPromotion, LateRsvpChange, WalkIn, PlanningShortfall, Other
    };
}

// SEAT-01 lifecycle: the kind is fixed at creation and decides which statuses a table may hold.
public static class TableLifecycle
{
    public static string InitialStatus(string kind) =>
        kind == TableKinds.Backup ? TableStatuses.Backup : TableStatuses.Planned;

    // GOV-01 R1-08: only ACTIVE tables receive guests; PLANNED is the booked-but-unconfirmed layout.
    public static bool IsSeatable(string status) => status == TableStatuses.Active;

    public static bool IsAllowed(string kind, string status) => kind == TableKinds.Backup
        ? status is TableStatuses.Backup or TableStatuses.Active or TableStatuses.Inactive
        : status is TableStatuses.Planned or TableStatuses.Active or TableStatuses.Inactive;

    // A table created without an explicit kind is a backup table only when it starts in BACKUP.
    public static string KindFor(string? kind, string status) =>
        kind ?? (status == TableStatuses.Backup ? TableKinds.Backup : TableKinds.Primary);

    public static bool CanTransition(string kind, string from, string to) => (from, to) switch
    {
        (TableStatuses.Planned, TableStatuses.Active) => kind == TableKinds.Primary,
        (TableStatuses.Backup, TableStatuses.Active) => kind == TableKinds.Backup,
        (TableStatuses.Planned, TableStatuses.Inactive) => true,
        (TableStatuses.Active, TableStatuses.Inactive) => true,
        (TableStatuses.Backup, TableStatuses.Inactive) => true,
        (TableStatuses.Inactive, TableStatuses.Planned) => kind == TableKinds.Primary,
        (TableStatuses.Inactive, TableStatuses.Backup) => kind == TableKinds.Backup,
        _ => false
    };

    public static bool IsBackupActivation(string from, string to) =>
        from == TableStatuses.Backup && to == TableStatuses.Active;

    // Restoring returns a table to the status matching its origin.
    public static string RestoreTarget(string kind) => InitialStatus(kind);
}
