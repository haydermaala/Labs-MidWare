using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <summary>
    /// Scope the two read-only auxiliary RLS policies to SELECT (ADR 0018 §6/§8).
    ///
    /// `CREATE POLICY … USING (…)` with no `FOR &lt;command&gt;` defaults to **FOR ALL**, and
    /// for a FOR ALL policy Postgres reuses the USING expression as the WITH CHECK when
    /// none is given. So `memberships_self_read` and `device_credentials_device_auth` —
    /// both documented as read-only, and both backing genuinely read-only code paths
    /// (MembershipService.MembershipsFor, EfControlPlaneStore.ValidateDeviceCredential,
    /// each AsNoTracking) — were in fact write-capable.
    ///
    /// That matters most on `memberships`: permissive policies are OR'd, so with
    /// `app.user_id` bound, a row carrying that UserId satisfied the check for ANY
    /// TenantId — the isolation policy could be bypassed for writes. RLS is the
    /// defence-in-depth layer behind the app-layer scope, so it must actually hold.
    ///
    /// The other two auxiliary policies (bootstrap_tokens_device_auth,
    /// invitations_token_auth) legitimately need writes — enrollment marks a token used
    /// and acceptance marks an invitation consumed — and each already carries an explicit
    /// WITH CHECK equal to its USING, so a caller can only write the row whose secret it
    /// presented. Those are left as FOR ALL deliberately.
    /// </summary>
    public partial class TightenAuxiliaryRlsPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.Ordinal))
            {
                return;
            }

            migrationBuilder.Sql("DROP POLICY IF EXISTS memberships_self_read ON memberships;");
            migrationBuilder.Sql(
                "CREATE POLICY memberships_self_read ON memberships FOR SELECT "
                + "USING (\"UserId\" = current_setting('app.user_id', true));");

            migrationBuilder.Sql("DROP POLICY IF EXISTS device_credentials_device_auth ON device_credentials;");
            migrationBuilder.Sql(
                "CREATE POLICY device_credentials_device_auth ON device_credentials FOR SELECT "
                + "USING (\"GatewayId\" = current_setting('app.device_gateway', true) "
                + "AND \"Credential\" = current_setting('app.device_credential', true));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (!migrationBuilder.ActiveProvider.Contains("Npgsql", System.StringComparison.Ordinal))
            {
                return;
            }

            // Restore the original (FOR ALL) shape.
            migrationBuilder.Sql("DROP POLICY IF EXISTS memberships_self_read ON memberships;");
            migrationBuilder.Sql(
                "CREATE POLICY memberships_self_read ON memberships "
                + "USING (\"UserId\" = current_setting('app.user_id', true));");

            migrationBuilder.Sql("DROP POLICY IF EXISTS device_credentials_device_auth ON device_credentials;");
            migrationBuilder.Sql(
                "CREATE POLICY device_credentials_device_auth ON device_credentials "
                + "USING (\"GatewayId\" = current_setting('app.device_gateway', true) "
                + "AND \"Credential\" = current_setting('app.device_credential', true));");
        }
    }
}
