SQL emitted by the slow endpoint (N+1 — one roundtrip per author):

info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (5ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
      SELECT [a].[Id], [a].[Name]
      FROM [Authors] AS [a]

info: 29-05-2026 11:05:35.512 RelationalEventId.CommandExecuted[20101] (Microsoft.EntityFrameworkCore.Database.Command) 
      Executed DbCommand (7ms) [Parameters=[@author_Id='10'], CommandType='Text', CommandTimeout='30']
      SELECT [q].[Id], [q].[Author], [q].[AuthorId], [q].[CreatedByEmail], [q].[IsDeleted], [q].[Text]
      FROM [Quotes] AS [q]
      WHERE [q].[AuthorId] = @author_Id

-- ^ This second query fired once per author. With 10 authors = 11 total roundtrips per HTTP request.


SQL emitted by the fast endpoint (single statement — one roundtrip total):

SELECT [a].[Id], [a].[Name], (
    SELECT COUNT(*)
    FROM [Quotes] AS [q]
    WHERE [a].[Id] = [q].[AuthorId]
) AS [QuoteCount]
FROM [Authors] AS [a]

-- EF Core translates .Select(a => new { QuoteCount = a.Quotes.Count }) as a correlated subquery.
-- SQL Server satisfies each per-author COUNT via an Index Seek on IX_Quotes_AuthorId_Covering —
-- no Key Lookup, no full table scan, no extra application roundtrips.





P(90)-P(95) | Slow api / Before-Fast Api : 


  █ TOTAL RESULTS 

    HTTP
    http_req_duration..............: avg=1.4s min=131.31ms med=1.3s max=4.49s p(90)=2.18s p(95)=2.34s p(99)=3.35s
      { expected_response:true }...: avg=1.4s min=131.31ms med=1.3s max=4.49s p(90)=2.18s p(95)=2.34s p(99)=3.35s
    http_req_failed................: 0.00% 0 out of 435
    http_reqs......................: 435   14.102861/s

    EXECUTION
    iteration_duration.............: avg=1.4s min=131.83ms med=1.3s max=4.51s p(90)=2.18s p(95)=2.34s p(99)=3.36s
    iterations.....................: 435   14.102861/s
    vus............................: 20    min=20       max=20
    vus_max........................: 20    min=20       max=20

    NETWORK
    data_received..................: 34 MB 1.1 MB/s
    data_sent......................: 41 kB 1.3 kB/s



After-Fast Api (covering index + single SQL statement):


  █ TOTAL RESULTS

    HTTP
    http_req_duration..............: avg=20.67ms min=4.13ms med=14.43ms max=1.98s p(90)=33.7ms  p(95)=46.6ms  p(99)=97.28ms
      { expected_response:true }...: avg=20.67ms min=4.13ms med=14.43ms max=1.98s p(90)=33.7ms  p(95)=46.6ms  p(99)=97.28ms
    http_req_failed................: 0.00%  0 out of 28785
    http_reqs......................: 28785  959.280571/s

    EXECUTION
    iteration_duration.............: avg=20.81ms min=4.4ms  med=14.51ms max=2.04s p(90)=33.85ms p(95)=46.85ms p(99)=97.28ms
    iterations.....................: 28785  959.280571/s
    vus............................: 20     min=20         max=20
    vus_max........................: 20     min=20         max=20

    NETWORK
    data_received..................: 19 MB  622 kB/s
    data_sent......................: 3.0 MB 101 kB/s


running (0m30.0s), 00/20 VUs, 28785 complete and 0 interrupted iterations
default ✓ [======================================] 20 VUs  30s


Improvement summary:
  p99:        3,350 ms  →  97.28 ms   (34.4× faster)
  p95:        2,340 ms  →  46.6 ms    (50.2× faster)
  p90:        2,180 ms  →  33.7 ms    (64.7× faster)
  throughput:   14.1 /s → 959.3 /s   (68× more requests/s)
  requests:      435    →  28,785     (same 30s window, 20 VUs)
  errors:       0.00%   →   0.00%


Changes Made
1. Eliminated the N+1 Query Pattern

The original endpoint loaded all authors and then executed a separate query for each author's quotes:

var authors = await db.Authors.ToListAsync();

foreach (var author in authors)
{
    var quotes = await db.Quotes
        .Where(q => EF.Property<int>(q, "AuthorId") == author.Id)
        .ToListAsync();
}

This resulted in multiple database round-trips and poor performance under load.

2. Added a Covering Index on AuthorId

CREATE INDEX IX_Quotes_AuthorId_Covering
ON Quotes(AuthorId)
INCLUDE (IsDeleted, Text);

The plain IX_Quotes_AuthorId would enable an Index Seek but still require a Key Lookup per matching
row to retrieve Text and IsDeleted from the clustered index. Adding those columns via INCLUDE embeds
them in the index leaf pages — the correlated subquery COUNT can be satisfied entirely from index
pages with zero Key Lookups.
Execution plan after: Index Seek on IX_Quotes_AuthorId_Covering, no Key Lookup.
Logical reads: Quotes = 39, Authors = 3.

3. Switched to a Projection-Based Query

Instead of loading full entity graphs, only the required fields were returned:

app.MapGet("/fast-authors-with-quotes-projection",
    async (AppDbContext db) =>
{
    var result = await db.Authors
        .AsNoTracking()
        .Select(a => new
        {
            a.Id,
            a.Name,
            QuoteCount = a.Quotes.Count
        })
        .ToListAsync();

    return Results.Ok(result);
});
4. Used AsNoTracking()
.AsNoTracking()

Since the endpoint is read-only, change tracking was unnecessary and removing it reduced EF Core overhead.