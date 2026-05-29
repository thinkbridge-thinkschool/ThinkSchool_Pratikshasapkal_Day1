using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Replace the basic IX_Quotes_AuthorId (index seek but still needs key lookups for
            // Text and IsDeleted) with a covering index that stores those columns inline.
            // Result: the projection query SELECT AuthorId, Id, Text WHERE IsDeleted=0 is
            // satisfied entirely from the index leaf pages — zero key lookups per quote row.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_Quotes_AuthorId' AND object_id = OBJECT_ID('Quotes')
                )
                    DROP INDEX IX_Quotes_AuthorId ON Quotes;

                CREATE INDEX IX_Quotes_AuthorId_Covering
                    ON Quotes (AuthorId)
                    INCLUDE (IsDeleted, Text);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_Quotes_AuthorId_Covering' AND object_id = OBJECT_ID('Quotes')
                )
                    DROP INDEX IX_Quotes_AuthorId_Covering ON Quotes;

                CREATE INDEX IX_Quotes_AuthorId ON Quotes (AuthorId);
                """);
        }
    }
}
