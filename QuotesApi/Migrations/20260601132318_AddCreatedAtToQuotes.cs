using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedAtToQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the index only if it still exists (the previous failed run may have already dropped it).
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.indexes
                    WHERE name = 'IX_Quotes_AuthorId_Covering' AND object_id = OBJECT_ID('Quotes')
                )
                    DROP INDEX IX_Quotes_AuthorId_Covering ON Quotes;
                """);

            // Add CreatedAt only if it doesn't already exist (the column may have been added manually).
            migrationBuilder.Sql("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE Name = 'CreatedAt' AND Object_ID = OBJECT_ID('Quotes')
                )
                    ALTER TABLE Quotes ADD CreatedAt datetime2 NOT NULL DEFAULT '0001-01-01T00:00:00.0000000';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_AuthorId_Covering",
                table: "Quotes",
                column: "AuthorId")
                .Annotation("SqlServer:Include", new[] { "Text", "IsDeleted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Quotes_AuthorId_Covering",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Quotes");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_AuthorId_Covering",
                table: "Quotes",
                column: "AuthorId")
                .Annotation("SqlServer:Include", new[] { "IsDeleted", "Text" });
        }
    }
}
