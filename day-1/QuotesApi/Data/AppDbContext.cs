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


    public DbSet<User> Users => Set<User>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Author> Authors => Set<Author>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();
    public DbSet<OutboxMessage>    OutboxMessages    => Set<OutboxMessage>();

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

        // Relationship: Author → Quotes via shadow FK "AuthorId" on Quotes table.
        // Deliberately NO HasIndex("AuthorId") to force a table scan and demonstrate
        // the missing-index performance problem.
        modelBuilder.Entity<Author>()
            .HasMany(a => a.Quotes)
            .WithOne()
            .HasForeignKey("AuthorId")
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);

        // Idempotency: one record per (MessageId, Subscription) pair.
        // Two subscriptions each receive every message so the MessageId alone is
        // not unique across the whole table.
        modelBuilder.Entity<ProcessedMessage>()
            .HasIndex(m => new { m.MessageId, m.Subscription })
            .IsUnique()
            .HasDatabaseName("IX_ProcessedMessages_MessageId_Subscription");

        // Relay poll: WHERE SentAtUtc IS NULL ORDER BY CreatedAtUtc.
        // A filtered index on NULL rows keeps the scan tiny as the table grows.
        modelBuilder.Entity<OutboxMessage>()
            .HasIndex(m => m.SentAtUtc)
            .HasDatabaseName("IX_OutboxMessages_SentAtUtc");

        // Covering index: AuthorId (seek key) + Text, IsDeleted (INCLUDE columns).
        // Eliminates the Key Lookup that IX_Quotes_AuthorId alone causes — the query
        // SELECT AuthorId, Id, Text WHERE IsDeleted=0 is now satisfied entirely from
        // the index leaf pages, no round-trip to the clustered index per quote row.
        modelBuilder.Entity<Quote>()
            .HasIndex("AuthorId")
            .HasDatabaseName("IX_Quotes_AuthorId_Covering")
            .IncludeProperties(q => new { q.Text, q.IsDeleted });
    }
}