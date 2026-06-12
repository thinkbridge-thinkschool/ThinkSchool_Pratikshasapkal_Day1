-- Session 1: Update Quote Id 1 and then try to update Quote Id 2


BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Locked By Session 1'
WHERE Id = 1;

UPDATE Quotes
SET QuoteText = 'Session 1 Wants Row 2'
WHERE Id = 2;

Rollback;


-- Fixed Script

BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Session 1 Row 1'
WHERE Id = 1;

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET QuoteText = 'Session 1 Row 2'
WHERE Id = 2;

COMMIT;

