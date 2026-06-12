SET STATISTICS IO ON;
SET STATISTICS TIME ON;

SELECT [a].[Id], [a].[Name], (
    SELECT COUNT(*)
    FROM [Quotes] AS [q]
    WHERE [a].[Id] = [q].[AuthorId]
) AS [QuoteCount]
FROM [Authors] AS [a];