--Isolation Levels Examples

--Session2

SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT *
FROM Quotes
WHERE Id = 1;

--Non-Repeatable Read
UPDATE Quotes
SET QuoteText = 'Updated Between Reads'
WHERE Id = 2;



--Phantom Read
INSERT INTO Quotes (
    Id,
    Author,
    QuoteText,
    CreatedAt
)
VALUES (
    999999,
    'Albert Einstein',
    'Phantom Read Example',
    GETDATE()
);


