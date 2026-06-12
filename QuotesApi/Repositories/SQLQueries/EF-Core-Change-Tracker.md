# Day 10 — EF Core Change Tracker + AsNoTracking

## Tracked Query

```csharp
var trackedQuotes = await context.Quotes
    .OrderBy(q => q.Id)
    .Take(10000)
    .ToListAsync();
```

## AsNoTracking Query

```csharp
var noTrackQuotes = await context.Quotes
    .AsNoTracking()
    .OrderBy(q => q.Id)
    .Take(10000)
    .ToListAsync();
```

## Benchmark Results

| Query Type         | Rows  | Time    | Allocated Memory |
| ------------------ | ----- | ------- | ---------------- |
| Tracked Query      | 10000 | 1325 ms | 11283312 bytes   |
| AsNoTracking Query | 10000 | 194 ms  | 3600632 bytes    |

## Observation

`AsNoTracking()` significantly reduced both execution time and memory allocation because EF Core skipped change tracking for entities being read.

## When NOT to use AsNoTracking

Do not use `AsNoTracking()` when entities need to be updated because EF Core will not track changes automatically.
