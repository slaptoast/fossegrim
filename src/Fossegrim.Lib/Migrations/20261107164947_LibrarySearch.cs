using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace fossegrim.lib.Migrations
{
    /// <summary>
    /// Full-text indexes for library search (see SearchIndex). They aren't
    /// part of the EF model; the app fills them at startup and rebuilds them
    /// after scans and tag edits. remove_diacritics 2 makes "bjork" match
    /// "Björk".
    /// </summary>
    public partial class LibrarySearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "TrackSearch" USING fts5(
                    "Id" UNINDEXED, "Title", "Artist", "Album", "Genre", "Year",
                    tokenize = 'unicode61 remove_diacritics 2');
                """);
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "AlbumSearch" USING fts5(
                    "Id" UNINDEXED, "Name", "Artists", "Genre", "Year", "Label",
                    tokenize = 'unicode61 remove_diacritics 2');
                """);
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "ArtistSearch" USING fts5(
                    "Id" UNINDEXED, "Name",
                    tokenize = 'unicode61 remove_diacritics 2');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE \"ArtistSearch\";");
            migrationBuilder.Sql("DROP TABLE \"AlbumSearch\";");
            migrationBuilder.Sql("DROP TABLE \"TrackSearch\";");
        }
    }
}
