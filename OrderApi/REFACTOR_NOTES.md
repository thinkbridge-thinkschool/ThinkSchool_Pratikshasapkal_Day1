====  Refactor Notes ===

1 : Giant Controller Method

consequence : Everything is under one single Post action, hard to read and understand.
Fix     : Spliting logic into proper data layers like controller, service, and repository.
______________________________________________________________________________________________

2 : Direct DbContext creation inside controller

consequence : Because of tight coupling there will be dificult testability.
fix     : Using Dependency Injection instead of manual object creation.
___________________________________________________________________

3 : Synchronous EF Core calls inside async action

consequence : Can Blocks threads and reduces performance.
fix : Replacing with async EF methods like SaveChangesAsync() 
          await them.
__________________________________________________________________

4 : Empty catch{ } blocks

consequence : Exception swallowing. Impossible to track bugs because   code runs without errors but with bugs/ corrupted state.
fix : Using specific catch blocks with logging
___________________________________________________________________

5 : Duplicate validation logic

consequence : Same checks repeated multiple times result in unnecessary code lengthening.
fix : Centralizing validation into reusable methods.
___________________________________________________________________

6 : Possible null reference bug

consequence : req.Customer may be null and crash application. Can throw runtime NullReferenceException.
fix : Adding proper null checks and correct validation.
___________________________________________________________________

7 : Off-by-one loop bug

consequence : Loop using <= which can cause index out of range exception
fix : Replacing condition to < with <= .
___________________________________________________________________

8 : Duplicate total calculation logic

consequence : Total is calculated multiple times resulting unnecessarily increasing code length and redusing performance.
fix : Creating single reusable calculation method.
___________________________________________________________________

9 : Returning object instead of typed response

consequence : unclear API response 
fix : Using ActionResult<T> or typed DTO responses.
___________________________________________________________________

10 : No cancellation token usage

consequence : Long-running requests cannot be cancelled properly. Server won't stop running without custom stopping mechanism.
fix : Passing CancellationToken through all async methods.
___________________________________________________________________


11 : No transaction handling

consequence : Stock update and order save can become inconsistent.
fix : Using database transactions. Using a single transaction around the whole workflow.
___________________________________________________________________

12 : Logging done with Console.WriteLine

consequence : Not suitable for production logging.
fix : Use ILogger<T>.
___________________________________________________________________

13 : No tests present

consequence : Bugs can easily go unnoticed.
fix : Adding unit tests and integration tests.
___________________________________________________________________
