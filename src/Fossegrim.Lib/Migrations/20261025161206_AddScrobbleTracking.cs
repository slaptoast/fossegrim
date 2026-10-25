using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class AddScrobbleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NextScrobbleAttemptAt",
                table: "PlayEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScrobbleAttempts",
                table: "PlayEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ScrobbleError",
                table: "PlayEvents",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScrobbleStatus",
                table: "PlayEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlayEvents_ScrobbleStatus_NextScrobbleAttemptAt",
                table: "PlayEvents",
                columns: new[] { "ScrobbleStatus", "NextScrobbleAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PlayEvents_ScrobbleStatus_NextScrobbleAttemptAt",
                table: "PlayEvents");

            migrationBuilder.DropColumn(
                name: "NextScrobbleAttemptAt",
                table: "PlayEvents");

            migrationBuilder.DropColumn(
                name: "ScrobbleAttempts",
                table: "PlayEvents");

            migrationBuilder.DropColumn(
                name: "ScrobbleError",
                table: "PlayEvents");

            migrationBuilder.DropColumn(
                name: "ScrobbleStatus",
                table: "PlayEvents");
        }
    }
}
