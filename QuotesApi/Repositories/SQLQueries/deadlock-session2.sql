
-- Session 2: Update Quote Id 2 and then try to update Quote Id 1

BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Locked By Session 2'
WHERE Id = 2;

UPDATE Quotes
SET QuoteText = 'Session 2 Wants Row 1'
WHERE Id = 1;


--Fixed Script
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Session 2 Row 1'
WHERE Id = 1;

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET QuoteText = 'Session 2 Row 2'
WHERE Id = 2;

COMMIT;

--



--EF Core change tracker + AsNoTracking

--10k rows in Quotes table
WITH Numbers AS
(
    SELECT TOP 10000
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.objects a
    CROSS JOIN sys.objects b
)
INSERT INTO Quotes (Id, Author, QuoteText, CreatedAt)
SELECT
    n + 100000,
    CONCAT('Author ', n),
    CONCAT('Benchmark Quote ', n),
    GETDATE()
FROM Numbers;

SELECT COUNT(*) FROM Quotes;

SELECT TOP 5 QuoteText
FROM Quotes;

--SELECT COUNT(*)
--FROM Quotes
--HERE [Text] IS NULL;