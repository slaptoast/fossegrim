using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaFolderEntity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MediaFolderId",
                table: "MediaItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: false),
                    DateAdded = table.Column<DateTime>(type: "TEXT", nullable: true, defaultValueSql: "datetime('now')"),
                    DateLastScanned = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFolders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaItems_MediaFolderId",
                table: "MediaItems",
                column: "MediaFolderId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFolders_Location",
                table: "MediaFolders",
                column: "Location",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFolders_Name",
                table: "MediaFolders",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaItems_MediaFolders_MediaFolderId",
                table: "MediaItems",
                column: "MediaFolderId",
                principalTable: "MediaFolders",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaItems_MediaFolders_MediaFolderId",
                table: "MediaItems");

            migrationBuilder.DropTable(
                name: "MediaFolders");

            migrationBuilder.DropIndex(
                name: "IX_MediaItems_MediaFolderId",
                table: "MediaItems");

            migrationBuilder.DropColumn(
                name: "MediaFolderId",
                table: "MediaItems");
        }
    }
}
