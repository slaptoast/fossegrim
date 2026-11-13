using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class UserNamesFromDisplayNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The display name becomes the username (Identity's UserName,
            // which held the email until now). NormalizedDisplayName was
            // already unique and in Identity's form. Accounts without a
            // display name keep their email as their username, as does one
            // whose name an email-named account already has (rare; the
            // unique index would otherwise stop the migration). Either can
            // pick a username on their Profile page.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "UserName" = trim("DisplayName"), "NormalizedUserName" = "NormalizedDisplayName"
                WHERE "NormalizedDisplayName" IS NOT NULL
                  AND "NormalizedDisplayName" NOT IN (
                      SELECT "other"."NormalizedUserName" FROM "AspNetUsers" AS "other"
                      WHERE "other"."NormalizedDisplayName" IS NULL AND "other"."NormalizedUserName" IS NOT NULL);
                """);

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedDisplayName",
                table: "AspNetUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedDisplayName",
                table: "AspNetUsers",
                type: "TEXT",
                nullable: true);

            // Back to display names, and the email as the Identity username.
            migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "DisplayName" = "UserName", "NormalizedDisplayName" = "NormalizedUserName",
                    "UserName" = "Email", "NormalizedUserName" = "NormalizedEmail";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_NormalizedDisplayName",
                table: "AspNetUsers",
                column: "NormalizedDisplayName",
                unique: true);
        }
    }
}
