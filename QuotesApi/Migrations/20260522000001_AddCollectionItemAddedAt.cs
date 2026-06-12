using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using QuotesApi.Data;

#nullable disable

namespace QuotesApi.Migrations
{
    // This migration adds the AddedAt column that was present in the EF Core model
    // but accidentally omitted from the InitialCreate migration. EnsureCreated() picked
    // it up automatically in earlier SQLite-based tests; Migrate() requires an explicit step.
    [DbContext(typeof(AppDbContext))]
    [Migration("20260522000001_AddCollectionItemAddedAt")]
    public class AddCollectionItemAddedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AddedAt",
                table: "CollectionItem",
                nullable: false,
                // Sentinel default for any pre-existing rows when applied to a live database.
                // Fresh test databases have no CollectionItem rows so this never fires in tests.
                defaultValue: new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddedAt",
                table: "CollectionItem");
        }
    }
}
