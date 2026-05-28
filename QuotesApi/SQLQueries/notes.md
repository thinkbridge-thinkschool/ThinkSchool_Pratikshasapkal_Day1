# Day 9 — Isolation Levels & Read Anomalies

## Database

SQL Server Management Studio (SSMS)

## Table Used

```sql
Quotes (
    Id,
    Author,
    QuoteText,
    CreatedAt
)
```

## Objective

Reproduce:

* Dirty Read
* Non-Repeatable Read
* Phantom Read

using two concurrent SQL sessions and identify the lowest isolation level that prevents each anomaly.

---

# Dirty Read

## What Happened

Session 2 was able to read uncommitted data modified by Session 1.

---

## Session 1

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Dirty Read Example'
WHERE Id = 1;
```

Transaction intentionally left uncommitted.

---

## Session 2

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT *
FROM Quotes
WHERE Id = 1;
```

Observed result:

```text
Dirty Read Example
```

even though Session 1 had not committed yet.

---

## Rollback

```sql
ROLLBACK;
```

After rollback, the original quote value returned.

---

## Observation

`READ UNCOMMITTED` allows dirty reads because it permits reading temporary uncommitted changes from other transactions.

---

# Non-Repeatable Read

## What Happened

The same query inside the same transaction returned different values because another session updated the row between reads.

---

## Session 1

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

BEGIN TRANSACTION;

SELECT QuoteText
FROM Quotes
WHERE Id = 2;
```

First result:

```text
Imagination is more important than knowledge.
```

---

## Session 2

```sql
UPDATE Quotes
SET QuoteText = 'Updated Between Reads'
WHERE Id = 2;
```

---

## Session 1 Again

```sql
SELECT QuoteText
FROM Quotes
WHERE Id = 2;
```

Second result:

```text
Updated Between Reads
```

---

## Observation

The same row returned different values inside the same transaction because another transaction committed an update in between the reads.

---

# Phantom Read

## What Happened

A new row appeared inside the same transaction because another session inserted matching data.

---

## Session 1

```sql
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;

BEGIN TRANSACTION;

SELECT *
FROM Quotes
WHERE Author = 'Albert Einstein';
```

Initial result:

```text
2502 rows
```

---

## Session 2

```sql
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
```

---

## Session 1 Again

```sql
SELECT *
FROM Quotes
WHERE Author = 'Albert Einstein';
```

Second result:

```text
2503 rows
```

---

## Observation

A new matching row appeared during the same transaction. `REPEATABLE READ` protects existing rows but does not prevent new matching rows from being inserted.

---

# Isolation Level Table

| Anomaly             | Lowest Isolation Level Preventing It |
| ------------------- | ------------------------------------ |
| Dirty Read          | READ COMMITTED                       |
| Non-Repeatable Read | REPEATABLE READ                      |
| Phantom Read        | SERIALIZABLE                         |

---

# What I Learned

* `READ UNCOMMITTED` allows unsafe reads of temporary transaction data.
* `READ COMMITTED` prevents dirty reads but still allows data changes between reads.
* `REPEATABLE READ` prevents updates to previously read rows but still allows phantom rows.
* `SERIALIZABLE` provides the highest consistency by preventing all three anomalies.
* Higher isolation levels improve consistency but reduce concurrency and increase locking.

---

# What Would Break This

* Running queries in the wrong order would fail to reproduce anomalies.
* Forgetting `BEGIN TRANSACTION` would auto-commit changes immediately.
* Using snapshot isolation would change transaction behavior.
* Long-running serializable transactions could increase blocking and deadlocks.
