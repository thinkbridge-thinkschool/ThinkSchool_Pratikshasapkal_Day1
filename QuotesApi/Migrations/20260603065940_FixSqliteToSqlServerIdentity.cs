using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuotesApi.Migrations
{
    /// <inheritdoc />
    public partial class FixSqliteToSqlServerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Background
            // ──────────
            // Several early migrations were generated against SQLite.
            // When they ran on SQL Server the SQLite-specific annotations were
            // silently ignored, so the Id columns on Quotes, Collections, Users,
            // and RefreshTokens were created as INT NOT NULL without IDENTITY.
            //
            // Additionally, Collections and CollectionItem are entirely absent
            // from the Azure SQL database (schema drift from partial migrations).
            //
            // Every block below is fully idempotent:
            //   • If a table is missing   → CREATE it with the correct schema.
            //   • If a table exists with   IDENTITY → skip (already correct).
            //   • If a table exists without IDENTITY → recreate it, preserving data.
            //
            // Order matters: Collections must exist before CollectionItem
            // so that the FK in CollectionItem can be validated.

            // ── 1. Collections ────────────────────────────────────────────────
            // Create with IDENTITY if table is absent.
            migrationBuilder.Sql("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'Collections') AND type = N'U'
                )
                BEGIN
                    CREATE TABLE [Collections] (
                        [Id]      INT           NOT NULL IDENTITY(1, 1),
                        [Name]    NVARCHAR(MAX) NOT NULL,
                        [OwnerId] INT           NOT NULL,
                        CONSTRAINT [PK_Collections] PRIMARY KEY ([Id])
                    );
                END;
                """);

            // Recreate with IDENTITY if table exists but lacks it.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'Collections') AND type = N'U'
                )
                BEGIN
                    IF COLUMNPROPERTY(OBJECT_ID(N'Collections'), 'Id', 'IsIdentity') = 0
                    BEGIN
                        IF OBJECT_ID(N'FK_CollectionItem_Collections_CollectionId', 'F') IS NOT NULL
                            ALTER TABLE [CollectionItem]
                                DROP CONSTRAINT [FK_CollectionItem_Collections_CollectionId];

                        CREATE TABLE [Collections_New] (
                            [Id]      INT           NOT NULL IDENTITY(1, 1),
                            [Name]    NVARCHAR(MAX) NOT NULL,
                            [OwnerId] INT           NOT NULL,
                            CONSTRAINT [PK_Collections_tmp] PRIMARY KEY ([Id])
                        );

                        SET IDENTITY_INSERT [Collections_New] ON;
                        INSERT INTO [Collections_New] ([Id], [Name], [OwnerId])
                        SELECT [Id], CAST([Name] AS NVARCHAR(MAX)), [OwnerId]
                        FROM   [Collections];
                        SET IDENTITY_INSERT [Collections_New] OFF;

                        DROP TABLE [Collections];
                        EXEC sp_rename N'Collections_New',    N'Collections';
                        EXEC sp_rename N'PK_Collections_tmp', N'PK_Collections', 'OBJECT';
                    END;
                END;
                """);

            // ── 2. CollectionItem ─────────────────────────────────────────────
            // Created WITHOUT the FK so this block never depends on Collections
            // being in a validated state.  The FK is added in the final block
            // after every table repair has completed.
            migrationBuilder.Sql("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'CollectionItem') AND type = N'U'
                )
                BEGIN
                    CREATE TABLE [CollectionItem] (
                        [CollectionId] INT       NOT NULL,
                        [Id]           INT       NOT NULL,
                        [QuoteId]      INT       NOT NULL,
                        [AddedAt]      DATETIME2 NOT NULL
                            DEFAULT '0001-01-01T00:00:00.0000000',
                        CONSTRAINT [PK_CollectionItem]
                            PRIMARY KEY ([CollectionId], [Id])
                    );
                END
                ELSE IF NOT EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE Name      = N'AddedAt'
                      AND Object_ID = OBJECT_ID(N'CollectionItem')
                )
                BEGIN
                    ALTER TABLE [CollectionItem]
                        ADD [AddedAt] DATETIME2 NOT NULL
                            DEFAULT '0001-01-01T00:00:00.0000000';
                END;
                """);

            // ── 3. Quotes ─────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'Quotes') AND type = N'U'
                )
                BEGIN
                    IF COLUMNPROPERTY(OBJECT_ID(N'Quotes'), 'Id', 'IsIdentity') = 0
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM sys.indexes
                            WHERE name      = N'IX_Quotes_AuthorId_Covering'
                              AND object_id = OBJECT_ID(N'Quotes')
                        )
                            DROP INDEX [IX_Quotes_AuthorId_Covering] ON [Quotes];

                        IF OBJECT_ID(N'FK_Quotes_Authors_AuthorId', 'F') IS NOT NULL
                            ALTER TABLE [Quotes]
                                DROP CONSTRAINT [FK_Quotes_Authors_AuthorId];

                        IF OBJECT_ID(N'Quotes_New', 'U') IS NOT NULL
                            DROP TABLE [Quotes_New];

                        CREATE TABLE [Quotes_New] (
                            [Id]             INT           NOT NULL IDENTITY(1, 1),
                            [Author]         NVARCHAR(MAX) NOT NULL,
                            [Text]           NVARCHAR(MAX) NOT NULL,
                            [IsDeleted]      BIT           NOT NULL DEFAULT 0,
                            [CreatedByEmail] NVARCHAR(MAX) NOT NULL DEFAULT '',
                            [CreatedAt]      DATETIME2     NOT NULL
                                DEFAULT '0001-01-01T00:00:00.0000000',
                            [AuthorId]       INT           NULL,
                            CONSTRAINT [PK_Quotes_tmp] PRIMARY KEY ([Id])
                        );

                        SET IDENTITY_INSERT [Quotes_New] ON;
                        INSERT INTO [Quotes_New]
                            ([Id], [Author], [Text], [IsDeleted],
                             [CreatedByEmail], [CreatedAt], [AuthorId])
                        SELECT
                            [Id],
                            CAST([Author] AS NVARCHAR(MAX)),
                            CAST([Text]   AS NVARCHAR(MAX)),
                            -- IsDeleted / CreatedByEmail / CreatedAt were added by
                            -- conditional migrations that may have been skipped if
                            -- the columns already existed as nullable.  ISNULL guards
                            -- replace any NULL values with the correct sentinels.
                            ISNULL(CAST([IsDeleted]      AS BIT),           0),
                            ISNULL(CAST([CreatedByEmail] AS NVARCHAR(MAX)), ''),
                            ISNULL(CAST([CreatedAt]      AS DATETIME2),
                                   '0001-01-01T00:00:00.0000000'),
                            [AuthorId]
                        FROM [Quotes];
                        SET IDENTITY_INSERT [Quotes_New] OFF;

                        DROP TABLE [Quotes];
                        EXEC sp_rename N'Quotes_New',    N'Quotes';
                        EXEC sp_rename N'PK_Quotes_tmp', N'PK_Quotes', 'OBJECT';

                        CREATE INDEX [IX_Quotes_AuthorId_Covering]
                            ON [Quotes] ([AuthorId])
                            INCLUDE ([Text], [IsDeleted]);

                        ALTER TABLE [Quotes]
                            ADD CONSTRAINT [FK_Quotes_Authors_AuthorId]
                            FOREIGN KEY ([AuthorId]) REFERENCES [Authors] ([Id])
                            ON DELETE SET NULL;
                    END;
                END;
                """);

            // ── 4. Users ──────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'Users') AND type = N'U'
                )
                BEGIN
                    CREATE TABLE [Users] (
                        [Id]           INT           NOT NULL IDENTITY(1, 1),
                        [Email]        NVARCHAR(MAX) NOT NULL,
                        [PasswordHash] NVARCHAR(MAX) NOT NULL,
                        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
                    );
                END
                ELSE IF COLUMNPROPERTY(OBJECT_ID(N'Users'), 'Id', 'IsIdentity') = 0
                BEGIN
                    IF OBJECT_ID(N'FK_RefreshTokens_Users_UserId', 'F') IS NOT NULL
                        ALTER TABLE [RefreshTokens]
                            DROP CONSTRAINT [FK_RefreshTokens_Users_UserId];

                    CREATE TABLE [Users_New] (
                        [Id]           INT           NOT NULL IDENTITY(1, 1),
                        [Email]        NVARCHAR(MAX) NOT NULL,
                        [PasswordHash] NVARCHAR(MAX) NOT NULL,
                        CONSTRAINT [PK_Users_tmp] PRIMARY KEY ([Id])
                    );

                    SET IDENTITY_INSERT [Users_New] ON;
                    INSERT INTO [Users_New] ([Id], [Email], [PasswordHash])
                    SELECT
                        [Id],
                        CAST([Email]        AS NVARCHAR(MAX)),
                        CAST([PasswordHash] AS NVARCHAR(MAX))
                    FROM [Users];
                    SET IDENTITY_INSERT [Users_New] OFF;

                    DROP TABLE [Users];
                    EXEC sp_rename N'Users_New',    N'Users';
                    EXEC sp_rename N'PK_Users_tmp', N'PK_Users', 'OBJECT';

                    ALTER TABLE [RefreshTokens]
                        ADD CONSTRAINT [FK_RefreshTokens_Users_UserId]
                        FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id])
                        ON DELETE CASCADE;
                END;
                """);

            // ── 5. RefreshTokens ──────────────────────────────────────────────
            migrationBuilder.Sql("""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'RefreshTokens') AND type = N'U'
                )
                BEGIN
                    CREATE TABLE [RefreshTokens] (
                        [Id]              INT           NOT NULL IDENTITY(1, 1),
                        [TokenHash]       NVARCHAR(MAX) NOT NULL,
                        [UserId]          INT           NOT NULL,
                        [ExpiresAt]       DATETIME2     NOT NULL,
                        [RevokedAt]       DATETIME2     NULL,
                        [ReplacedByToken] NVARCHAR(MAX) NULL,
                        [FamilyId]        NVARCHAR(MAX) NOT NULL,
                        CONSTRAINT [PK_RefreshTokens] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_RefreshTokens_Users_UserId]
                            FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id])
                            ON DELETE CASCADE
                    );

                    CREATE INDEX [IX_RefreshTokens_UserId]
                        ON [RefreshTokens] ([UserId]);
                END
                ELSE IF COLUMNPROPERTY(OBJECT_ID(N'RefreshTokens'), 'Id', 'IsIdentity') = 0
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM sys.indexes
                        WHERE name      = N'IX_RefreshTokens_UserId'
                          AND object_id = OBJECT_ID(N'RefreshTokens')
                    )
                        DROP INDEX [IX_RefreshTokens_UserId] ON [RefreshTokens];

                    CREATE TABLE [RefreshTokens_New] (
                        [Id]              INT           NOT NULL IDENTITY(1, 1),
                        [TokenHash]       NVARCHAR(MAX) NOT NULL,
                        [UserId]          INT           NOT NULL,
                        [ExpiresAt]       DATETIME2     NOT NULL,
                        [RevokedAt]       DATETIME2     NULL,
                        [ReplacedByToken] NVARCHAR(MAX) NULL,
                        [FamilyId]        NVARCHAR(MAX) NOT NULL,
                        CONSTRAINT [PK_RefreshTokens_tmp] PRIMARY KEY ([Id]),
                        CONSTRAINT [FK_RefreshTokens_Users_UserId_new]
                            FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id])
                            ON DELETE CASCADE
                    );

                    SET IDENTITY_INSERT [RefreshTokens_New] ON;
                    INSERT INTO [RefreshTokens_New]
                        ([Id], [TokenHash], [UserId], [ExpiresAt],
                         [RevokedAt], [ReplacedByToken], [FamilyId])
                    SELECT
                        [Id],
                        CAST([TokenHash]       AS NVARCHAR(MAX)),
                        [UserId],
                        CAST([ExpiresAt]       AS DATETIME2),
                        CAST([RevokedAt]       AS DATETIME2),
                        CAST([ReplacedByToken] AS NVARCHAR(MAX)),
                        CAST([FamilyId]        AS NVARCHAR(MAX))
                    FROM [RefreshTokens];
                    SET IDENTITY_INSERT [RefreshTokens_New] OFF;

                    DROP TABLE [RefreshTokens];
                    EXEC sp_rename N'RefreshTokens_New',    N'RefreshTokens';
                    EXEC sp_rename N'PK_RefreshTokens_tmp', N'PK_RefreshTokens', 'OBJECT';
                    EXEC sp_rename N'FK_RefreshTokens_Users_UserId_new',
                                   N'FK_RefreshTokens_Users_UserId', 'OBJECT';

                    CREATE INDEX [IX_RefreshTokens_UserId]
                        ON [RefreshTokens] ([UserId]);
                END;
                """);

            // ── 6. CollectionItem → Collections FK ───────────────────────────
            // Added last, after every table has been created or repaired.
            // Guards: CollectionItem exists, Collections exists, FK absent.
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'CollectionItem') AND type = N'U'
                )
                AND EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'Collections') AND type = N'U'
                )
                AND NOT EXISTS (
                    SELECT 1 FROM sys.foreign_keys
                    WHERE name = N'FK_CollectionItem_Collections_CollectionId'
                )
                BEGIN
                    ALTER TABLE [CollectionItem]
                        ADD CONSTRAINT [FK_CollectionItem_Collections_CollectionId]
                        FOREIGN KEY ([CollectionId])
                        REFERENCES [Collections] ([Id])
                        ON DELETE CASCADE;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Remove AddedAt if present (does not drop the table).
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.columns
                    WHERE Name      = N'AddedAt'
                      AND Object_ID = OBJECT_ID(N'CollectionItem')
                )
                    ALTER TABLE [CollectionItem] DROP COLUMN [AddedAt];
                """);

            // Reverse Quotes IDENTITY (recreate without IDENTITY).
            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM sys.objects
                    WHERE object_id = OBJECT_ID(N'Quotes') AND type = N'U'
                )
                BEGIN
                    IF COLUMNPROPERTY(OBJECT_ID(N'Quotes'), 'Id', 'IsIdentity') = 1
                    BEGIN
                        IF EXISTS (
                            SELECT 1 FROM sys.indexes
                            WHERE name      = N'IX_Quotes_AuthorId_Covering'
                              AND object_id = OBJECT_ID(N'Quotes')
                        )
                            DROP INDEX [IX_Quotes_AuthorId_Covering] ON [Quotes];

                        IF OBJECT_ID(N'FK_Quotes_Authors_AuthorId', 'F') IS NOT NULL
                            ALTER TABLE [Quotes]
                                DROP CONSTRAINT [FK_Quotes_Authors_AuthorId];

                        CREATE TABLE [Quotes_Old] (
                            [Id]             INT           NOT NULL,
                            [Author]         NVARCHAR(MAX) NOT NULL,
                            [Text]           NVARCHAR(MAX) NOT NULL,
                            [IsDeleted]      BIT           NOT NULL DEFAULT 0,
                            [CreatedByEmail] NVARCHAR(MAX) NOT NULL DEFAULT '',
                            [CreatedAt]      DATETIME2     NOT NULL
                                DEFAULT '0001-01-01T00:00:00.0000000',
                            [AuthorId]       INT           NULL,
                            CONSTRAINT [PK_Quotes_tmp] PRIMARY KEY ([Id])
                        );

                        INSERT INTO [Quotes_Old]
                            ([Id], [Author], [Text], [IsDeleted],
                             [CreatedByEmail], [CreatedAt], [AuthorId])
                        SELECT [Id], [Author], [Text], [IsDeleted],
                               [CreatedByEmail], [CreatedAt], [AuthorId]
                        FROM   [Quotes];

                        DROP TABLE [Quotes];
                        EXEC sp_rename N'Quotes_Old',    N'Quotes';
                        EXEC sp_rename N'PK_Quotes_tmp', N'PK_Quotes', 'OBJECT';

                        CREATE INDEX [IX_Quotes_AuthorId_Covering]
                            ON [Quotes] ([AuthorId]) INCLUDE ([Text], [IsDeleted]);

                        ALTER TABLE [Quotes]
                            ADD CONSTRAINT [FK_Quotes_Authors_AuthorId]
                            FOREIGN KEY ([AuthorId]) REFERENCES [Authors] ([Id])
                            ON DELETE SET NULL;
                    END;
                END;
                """);
        }
    }
}
