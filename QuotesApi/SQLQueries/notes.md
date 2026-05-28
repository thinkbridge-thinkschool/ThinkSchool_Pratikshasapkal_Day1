# Day 9 — Transaction Isolation Levels & Read Anomalies

**Database:** SQL Server (SSMS)  
**Table:** `Quotes` (`Id`, `Author`, `QuoteText`, `CreatedAt`)  
**Method:** Two concurrent query tabs (Session 1 and Session 2) to reproduce each anomaly.

---

## Background

SQL Server provides four standard isolation levels that control how transactions see data modified by other concurrent transactions:

| Isolation Level      | Description                                                                    |
|----------------------|--------------------------------------------------------------------------------|
| `READ UNCOMMITTED`   | Reads dirty (uncommitted) data. Lowest isolation, highest concurrency.         |
| `READ COMMITTED`     | Default. Only reads committed data. Prevents dirty reads.                      |
| `REPEATABLE READ`    | Holds read locks until transaction ends. Prevents dirty + non-repeatable reads.|
| `SERIALIZABLE`       | Fully serializes transactions. Prevents all three read anomalies.              |

Higher isolation levels improve data consistency but reduce concurrency because locks are held longer and block other transactions.

---

## Anomaly 1 — Dirty Read

### What It Is

A dirty read occurs when Transaction A reads data that Transaction B has written but **not yet committed**. If B later rolls back, A has read data that never actually existed.

### Session 1 (Writer — begins but does not commit)

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

BEGIN TRANSACTION;

INSERT INTO Quotes (Author, QuoteText, CreatedAt)
VALUES ('Test Author', 'This is a dirty read test.', GETDATE());

-- Do NOT commit yet. Switch to Session 2 now.
-- ROLLBACK TRANSACTION;
```

### Session 2 (Reader — uses READ UNCOMMITTED to see dirty data)

```sql
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT * FROM Quotes;
-- Returns the row inserted by Session 1 even though it is not committed.
```

### Observed Behavior

With `READ UNCOMMITTED`, Session 2 sees the row inserted by Session 1 before the commit. When Session 1 rolls back, that row disappears — Session 2 read data that never truly existed.

**Prevented by:** `READ COMMITTED` and above.

---

## Anomaly 2 — Non-Repeatable Read

### What It Is

A non-repeatable read occurs when Transaction A reads the same row twice and gets **different values** because Transaction B updated and committed that row between the two reads.

### Session 1 (Reader — reads the same row twice)

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

BEGIN TRANSACTION;

-- First read
SELECT * FROM Quotes WHERE Id = 1;

-- Switch to Session 2 and let it run, then come back here.

-- Second read (same row, different result)
SELECT * FROM Quotes WHERE Id = 1;

COMMIT;
```

### Session 2 (Writer — updates the row between Session 1's two reads)

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

BEGIN TRANSACTION;

UPDATE Quotes
SET Author = 'Updated Author'
WHERE Id = 1;

COMMIT;
```

### Observed Behavior

Session 1's first `SELECT` returns `Author = 'Original Author'`. After Session 2 commits its `UPDATE`, Session 1's second `SELECT` on the same row returns `Author = 'Updated Author'`. The same query inside the same transaction produced two different results.

**Prevented by:** `REPEATABLE READ` and above.

---

## Anomaly 3 — Phantom Read

### What It Is

A phantom read occurs when Transaction A runs the same range query twice and the **set of rows changes** between the two reads because Transaction B inserted or deleted rows that fall in the range.

### Session 1 (Reader — runs range query twice)

```sql
SET TRANSACTION ISOLATION LEVEL REPEATABLE READ;

BEGIN TRANSACTION;

-- First range read
SELECT * FROM Quotes WHERE Author = 'Marcus Aurelius';

-- Switch to Session 2 and let it run, then come back here.

-- Second range read (phantom row appears)
SELECT * FROM Quotes WHERE Author = 'Marcus Aurelius';

COMMIT;
```

### Session 2 (Writer — inserts a new row in the range)

```sql
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;

BEGIN TRANSACTION;

INSERT INTO Quotes (Author, QuoteText, CreatedAt)
VALUES ('Marcus Aurelius', 'New phantom quote.', GETDATE());

COMMIT;
```

### Observed Behavior

Session 1's first `SELECT` returns, say, 3 rows for `Author = 'Marcus Aurelius'`. After Session 2 inserts a new row for the same author, Session 1's second `SELECT` returns 4 rows. A new "phantom" row appeared inside an active transaction.

Note: `REPEATABLE READ` prevents updates to existing rows but still allows new inserts — phantom reads are only fully prevented by `SERIALIZABLE`, which locks the range itself.

**Prevented by:** `SERIALIZABLE`.

---

## Isolation Level Reference Table

| Anomaly               | Lowest Level That Prevents It |
|-----------------------|-------------------------------|
| Dirty Read            | `READ COMMITTED`              |
| Non-Repeatable Read   | `REPEATABLE READ`             |
| Phantom Read          | `SERIALIZABLE`                |

---

## What I Learned

- `READ UNCOMMITTED` allows all three anomalies and should almost never be used in production. It is occasionally used for quick reporting queries where approximate data is acceptable.
- `READ COMMITTED` (SQL Server's default) is a safe baseline — it prevents dirty reads without significantly blocking other transactions.
- `REPEATABLE READ` adds row-level read locks that persist for the transaction duration, preventing another session from updating rows you have already read. This increases blocking.
- `SERIALIZABLE` prevents phantom reads by locking the entire key range, effectively serializing access to any rows that match your query predicate. It offers maximum consistency at the cost of the lowest concurrency.
- Each higher isolation level trades concurrency for consistency. In high-throughput systems, lock contention under `SERIALIZABLE` can become a bottleneck, which is why many systems use optimistic concurrency or snapshot isolation (`READ COMMITTED SNAPSHOT`) instead.

---

## What Would Break This

| Scenario                                                                  | Effect                                                                         |
|---------------------------------------------------------------------------|--------------------------------------------------------------------------------|
| Running Session 2 too fast before Session 1 reaches its second read       | Anomaly may not be visible — timing is critical for reproduction.              |
| Forgetting `BEGIN TRANSACTION` in Session 1                               | Single statements auto-commit; no window exists for Session 2 to interleave.   |
| Using `SNAPSHOT` isolation level                                          | SQL Server uses row versioning instead of locks — anomalies behave differently. |
| `AUTOCOMMIT` mode on                                                      | Transactions close immediately, making multi-read anomalies impossible to see.  |
| Connection pooling or ORM abstracting isolation level                     | Application may silently override your `SET TRANSACTION ISOLATION LEVEL`.      |
| Uncommitted Session 1 transaction left open                               | Blocks other writes; can cause lock timeouts in shared environments.           |
