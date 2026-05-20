using Microsoft.EntityFrameworkCore;
using QuotesApi.Models;

namespace QuotesApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(
        DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Quote> Quotes => Set<Quote>();

    public DbSet<Collection> Collections => Set<Collection>();

    protected override void OnModelCreating(
        ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Collection>()
            .OwnsMany(x => x.Items, builder =>
            {
                builder.WithOwner()
                    .HasForeignKey("CollectionId");

                builder.Property<int>("Id");

                builder.HasKey(
                    "CollectionId",
                    "Id");
            });
    }
}