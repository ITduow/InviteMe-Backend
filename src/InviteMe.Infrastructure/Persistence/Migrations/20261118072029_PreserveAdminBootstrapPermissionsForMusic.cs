using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PreserveAdminBootstrapPermissionsForMusic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Materialize the legacy all-permissions bootstrap state. A configured
            // permission matrix always retains RBAC_VIEW and RBAC_MANAGE, so it
            // cannot consist solely of the two newly introduced music grants.
            migrationBuilder.Sql("""
                INSERT INTO inviteme.permissions (id, code, name, description, scope)
                SELECT gen_random_uuid(), code, name, description, 'PLATFORM'
                FROM (VALUES
                    ('ADMIN_DASHBOARD_VIEW', 'View platform dashboard', 'View aggregated platform counters.'),
                    ('ADMIN_USERS_VIEW', 'View accounts', 'Search and inspect user accounts.'),
                    ('ADMIN_USERS_MANAGE', 'Manage accounts', 'Suspend or reactivate accounts and assign platform roles.'),
                    ('ADMIN_WEDDINGS_VIEW', 'View weddings', 'Search platform weddings and their status.'),
                    ('ADMIN_TEMPLATES_VIEW', 'View templates', 'View invitation and wedding page templates.'),
                    ('ADMIN_TEMPLATES_MANAGE', 'Manage templates', 'Create, update, publish and hide templates.'),
                    ('ADMIN_AUDIT_VIEW', 'View platform audit log', 'View system-wide audit events.'),
                    ('ADMIN_RBAC_VIEW', 'View access control', 'View the role and permission model.'),
                    ('ADMIN_RBAC_MANAGE', 'Manage access control', 'Assign the fixed platform permission set to the Admin role.'),
                    ('ADMIN_PLANS_MANAGE', 'Manage plans and payments', 'Manage plans, orders and payments.')
                ) AS catalog(code, name, description)
                ON CONFLICT (code) DO NOTHING;

                INSERT INTO inviteme.role_permissions (role_id, permission_id)
                SELECT r.id, p.id FROM inviteme.roles r CROSS JOIN inviteme.permissions p
                WHERE r.normalized_name = 'ADMIN' AND p.scope = 'PLATFORM'
                  AND p.code IN ('ADMIN_DASHBOARD_VIEW', 'ADMIN_USERS_VIEW', 'ADMIN_USERS_MANAGE',
                      'ADMIN_WEDDINGS_VIEW', 'ADMIN_TEMPLATES_VIEW', 'ADMIN_TEMPLATES_MANAGE',
                      'ADMIN_AUDIT_VIEW', 'ADMIN_RBAC_VIEW', 'ADMIN_RBAC_MANAGE', 'ADMIN_PLANS_MANAGE')
                  AND NOT EXISTS (
                      SELECT 1 FROM inviteme.role_permissions g JOIN inviteme.permissions q ON q.id = g.permission_id
                      WHERE g.role_id = r.id AND q.code NOT IN ('ADMIN_MUSIC_VIEW', 'ADMIN_MUSIC_MANAGE'))
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
