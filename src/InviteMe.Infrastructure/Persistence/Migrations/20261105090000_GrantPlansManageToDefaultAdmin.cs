using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

public partial class GrantPlansManageToDefaultAdmin : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            INSERT INTO inviteme.permissions (code, name, description, scope)
            VALUES (
                'ADMIN_PLANS_MANAGE',
                'Manage plans and payments',
                'Edit plan prices and limits, review plan orders and payments, grant plans and view revenue.',
                'PLATFORM'
            )
            ON CONFLICT (code) DO UPDATE
            SET name = EXCLUDED.name,
                description = EXCLUDED.description,
                scope = 'PLATFORM';

            -- ADMIN's old complete default permission set did not include the plan-management feature.
            -- Add the new permission only when the existing role still has every permission from that
            -- default set and those grants predate this permission. An intentionally restricted Admin
            -- role remains restricted.
            INSERT INTO inviteme.role_permissions (role_id, permission_id)
            SELECT admin_role.id, plans_permission.id
            FROM inviteme.roles AS admin_role
            JOIN inviteme.permissions AS plans_permission
              ON plans_permission.code = 'ADMIN_PLANS_MANAGE'
             AND plans_permission.scope = 'PLATFORM'
            WHERE admin_role.code = 'ADMIN'
              AND EXISTS (
                  SELECT 1
                  FROM inviteme.role_permissions AS configured
                  JOIN inviteme.permissions AS configured_permission
                    ON configured_permission.id = configured.permission_id
                  WHERE configured.role_id = admin_role.id
                    AND configured_permission.scope = 'PLATFORM'
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM (VALUES
                      ('ADMIN_DASHBOARD_VIEW'),
                      ('ADMIN_USERS_VIEW'),
                      ('ADMIN_USERS_MANAGE'),
                      ('ADMIN_WEDDINGS_VIEW'),
                      ('ADMIN_TEMPLATES_VIEW'),
                      ('ADMIN_TEMPLATES_MANAGE'),
                      ('ADMIN_AUDIT_VIEW'),
                      ('ADMIN_RBAC_VIEW'),
                      ('ADMIN_RBAC_MANAGE')
                  ) AS previous_default(code)
                  WHERE NOT EXISTS (
                      SELECT 1
                      FROM inviteme.role_permissions AS configured
                      JOIN inviteme.permissions AS configured_permission
                        ON configured_permission.id = configured.permission_id
                      WHERE configured.role_id = admin_role.id
                        AND configured_permission.scope = 'PLATFORM'
                        AND configured_permission.code = previous_default.code
                  )
              )
              AND plans_permission.created_at > COALESCE(
                  (
                      SELECT MAX(configured.created_at)
                      FROM inviteme.role_permissions AS configured
                      JOIN inviteme.permissions AS configured_permission
                        ON configured_permission.id = configured.permission_id
                      WHERE configured.role_id = admin_role.id
                        AND configured_permission.scope = 'PLATFORM'
                        AND configured_permission.code IN (
                            'ADMIN_DASHBOARD_VIEW',
                            'ADMIN_USERS_VIEW',
                            'ADMIN_USERS_MANAGE',
                            'ADMIN_WEDDINGS_VIEW',
                            'ADMIN_TEMPLATES_VIEW',
                            'ADMIN_TEMPLATES_MANAGE',
                            'ADMIN_AUDIT_VIEW',
                            'ADMIN_RBAC_VIEW',
                            'ADMIN_RBAC_MANAGE'
                        )
                  ),
                  '-infinity'::timestamptz
              )
            ON CONFLICT (role_id, permission_id) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DELETE FROM inviteme.role_permissions AS grant_row
            USING inviteme.roles AS admin_role, inviteme.permissions AS plans_permission
            WHERE grant_row.role_id = admin_role.id
              AND admin_role.code = 'ADMIN'
              AND grant_row.permission_id = plans_permission.id
              AND plans_permission.code = 'ADMIN_PLANS_MANAGE';

            DELETE FROM inviteme.permissions AS plans_permission
            WHERE code = 'ADMIN_PLANS_MANAGE'
              AND NOT EXISTS (
                  SELECT 1 FROM inviteme.role_permissions
                  WHERE permission_id = plans_permission.id
              )
              AND NOT EXISTS (
                  SELECT 1 FROM inviteme.wedding_member_permissions
                  WHERE permission_id = plans_permission.id
              )
              AND NOT EXISTS (
                  SELECT 1 FROM inviteme.wedding_member_group_permissions
                  WHERE "PermissionId" = plans_permission.id
              )
              AND NOT EXISTS (
                  SELECT 1 FROM inviteme.cohost_invitation_grants
                  WHERE "PermissionId" = plans_permission.id
              );
            """);
    }
}
