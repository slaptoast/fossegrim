using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <inheritdoc />
    public partial class LibraryInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "InfoFetchedAt",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InfoUrl",
                table: "Artists",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InfoFetchedAt",
                table: "Albums",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InfoUrl",
                table: "Albums",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Summary",
                table: "Albums",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InfoFetchedAt",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "InfoUrl",
                table: "Artists");

            migrationBuilder.DropColumn(
                name: "InfoFetchedAt",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "InfoUrl",
                table: "Albums");

            migrationBuilder.DropColumn(
                name: "Summary",
                table: "Albums");
        }
    }
}
