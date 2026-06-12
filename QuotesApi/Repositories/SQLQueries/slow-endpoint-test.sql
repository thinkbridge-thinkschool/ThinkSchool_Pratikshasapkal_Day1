CREATE INDEX IX_Quotes_AuthorId
ON Quotes(AuthorId);

SELECT *
FROM Quotes
WHERE AuthorId = 1;

CREATE TABLE Users (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Email NVARCHAR(MAX) NOT NULL,
    PasswordHash NVARCHAR(MAX) NOT NULL
);

SELECT * FROM Users;

CREATE TABLE RefreshTokens (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    TokenHash NVARCHAR(MAX) NOT NULL,
    UserId INT NOT NULL,
    FamilyId NVARCHAR(MAX) NOT NULL,
    ExpiresAt DATETIME2 NOT NULL,
    RevokedAt DATETIME2 NULL,
    ReplacedByToken NVARCHAR(MAX) NULL,

    CONSTRAINT FK_RefreshTokens_Users
        FOREIGN KEY (UserId) REFERENCES Users(Id)
        ON DELETE CASCADE
);

SELECT Id, Email
FROM Users;