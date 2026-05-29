SQL emitted by the slow endpoint :

info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand (5ms) [Parameters=[], CommandType='Text', CommandTimeout='30']
      SELECT [a].[Id], [a].[Name]
      FROM [Authors] AS [a]

info: 29-05-2026 11:05:35.512 RelationalEventId.CommandExecuted[20101] (Microsoft.EntityFrameworkCore.Database.Command) 
      Executed DbCommand (7ms) [Parameters=[@author_Id='10'], CommandType='Text', CommandTimeout='30']
      SELECT [q].[Id], [q].[Author], [q].[AuthorId], [q].[CreatedByEmail], [q].[IsDeleted], [q].[Text]
      FROM [Quotes] AS [q]
      WHERE [q].[AuthorId] = @author_Id





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



P(90)-P(95) | After-Fast Api : 


  █ TOTAL RESULTS 

    HTTP
    http_req_duration..............: avg=20.95ms min=4.7ms med=13ms    max=1.17s p(90)=27.1ms  p(95)=40.54ms p(99)=221.34ms
      { expected_response:true }...: avg=20.95ms min=4.7ms med=13ms    max=1.17s p(90)=27.1ms  p(95)=40.54ms p(99)=221.34ms
    http_req_failed................: 0.00%  0 out of 28422
    http_reqs......................: 28422  946.773148/s

    EXECUTION
    iteration_duration.............: avg=21.08ms min=4.7ms med=13.11ms max=1.17s p(90)=27.26ms p(95)=40.67ms p(99)=221.34ms
    iterations.....................: 28422  946.773148/s
    vus............................: 20     min=20         max=20
    vus_max........................: 20     min=20         max=20

    NETWORK
    data_received..................: 18 MB  614 kB/s
    data_sent......................: 3.0 MB 99 kB/s




running (0m30.0s), 00/20 VUs, 28422 complete and 0 interrupted iterations
default ✓ [======================================] 20 VUs  30s


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

2. Added an Index on AuthorId
CREATE INDEX IX_Quotes_AuthorId
ON Quotes(AuthorId);

This allowed SQL Server to seek directly to matching rows instead of scanning the entire table.

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