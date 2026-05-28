IF OBJECT_ID('dbo.Quotes', 'U') IS NOT NULL
    DROP TABLE dbo.Quotes;

Create TABLE Quotes (
    id int primary key,
    Author varchar(255),
    QuoteText NVARCHAR(MAX),
    CreatedAt DATETIME2
)


INSERT INTO Quotes (Id, Author, QuoteText, CreatedAt)
VALUES
(1, 'Albert Einstein', 'Life is like riding a bicycle.', '2026-01-01'),
(2, 'Albert Einstein', 'Imagination is more important than knowledge.', '2026-02-01'),
(3, 'Marcus Aurelius', 'You have power over your mind.', '2026-03-01'),
(4, 'Marcus Aurelius', 'Waste no more time arguing.', '2026-04-01'),
(5, 'Confucius', 'Life is really simple.', '2026-05-01');


WITH Numbers AS
(
    SELECT TOP 100000
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.objects a
    CROSS JOIN sys.objects b
)
INSERT INTO Quotes (Id, Author, QuoteText, CreatedAt)
SELECT
    n + 100,
    CASE
        WHEN n % 2 = 0 THEN 'Albert Einstein'
        ELSE 'Marcus Aurelius'
    END,
    CONCAT('Generated quote ', n),
    DATEADD(DAY, n, '2026-01-01')
FROM Numbers;

;

SET STATISTICS IO ON;

SELECT
    Author,
    QuoteText,
    CreatedAt
FROM Quotes
WHERE Author = 'Albert Einstein';


SELECT
    Author,
    QuoteText,
    CreatedAt
FROM Quotes WITH (INDEX(IX_Quotes_Author_Covering))
WHERE Author = 'Albert Einstein';



--Isolation Levels Examples
--Session1
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Dirty Read Example'
WHERE Id = 1;

ROLLBACK;

--Non-Repeatable Read
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

BEGIN TRANSACTION;

SELECT QuoteText
FROM Quotes
WHERE Id = 2;

COMMIT;


--Phantom Read
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;

BEGIN TRANSACTION;

SELECT *
FROM Quotes
WHERE Author = 'Albert Einstein';

COMMIT;


