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





P(90)-P(95) : 


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

