CREATE INDEX IX_Quotes_AuthorId
ON Quotes(AuthorId);

SELECT *
FROM Quotes
WHERE AuthorId = 1;