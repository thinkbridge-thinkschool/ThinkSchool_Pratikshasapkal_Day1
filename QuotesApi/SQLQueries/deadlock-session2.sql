
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

