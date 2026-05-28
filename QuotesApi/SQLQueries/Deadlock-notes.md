# Day 9 — Reproduce and Resolve a Deadlock

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

---

# Objective

Reproduce a classic two-resource deadlock using two concurrent SQL sessions and resolve it using consistent lock ordering.

---

# What Is a Deadlock?

A deadlock occurs when:

* Session 1 holds a lock needed by Session 2
* Session 2 holds a lock needed by Session 1

Both sessions wait forever in a circular dependency.

SQL Server automatically detects the deadlock and terminates one transaction as the deadlock victim.

---

# Deadlock Reproduction

## Session 1

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Locked By Session 1'
WHERE Id = 1;

UPDATE Quotes
SET QuoteText = 'Session 1 Wants Row 2'
WHERE Id = 2;
```

---

## Session 2

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Locked By Session 2'
WHERE Id = 2;

UPDATE Quotes
SET QuoteText = 'Session 2 Wants Row 1'
WHERE Id = 1;
```

---

# Observed Behavior

Session 1 locked:

```text
Id = 1
```

Session 2 locked:

```text
Id = 2
```

Then:

* Session 1 attempted to access `Id = 2`
* Session 2 attempted to access `Id = 1`

This created a circular wait condition.

SQL Server detected the deadlock and automatically terminated one transaction with the error:

```text
Transaction was deadlocked on lock resources with another process and has been chosen as the deadlock victim.
```

---

# Cleanup

```sql
ROLLBACK;
```

Used to clear remaining active transactions after the deadlock.

---

# Deadlock Fix — Consistent Lock Ordering

## Session 1

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Session 1 Row 1'
WHERE Id = 1;

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET QuoteText = 'Session 1 Row 2'
WHERE Id = 2;

COMMIT;
```

---

## Session 2

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Session 2 Row 1'
WHERE Id = 1;

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET QuoteText = 'Session 2 Row 2'
WHERE Id = 2;

COMMIT;
```

---

# Why The Fix Works

The fix works because both transactions acquire locks in the same order (`Id = 1` → `Id = 2`), preventing circular wait conditions that cause deadlocks.

---

# What I Learned

* Deadlocks are caused by circular lock dependencies between concurrent transactions.
* SQL Server automatically selects a deadlock victim to break the cycle.
* Lock ordering is one of the simplest and most effective deadlock prevention strategies.
* Longer transactions increase the probability of deadlocks.
* `WAITFOR DELAY` is useful for reproducing concurrency issues intentionally.

---

# What Would Break This

* Running queries in the wrong order may fail to reproduce the deadlock.
* Forgetting `BEGIN TRANSACTION` would auto-commit updates immediately.
* Using only one session cannot reproduce a deadlock.
* Very fast execution timing may prevent the circular wait from occurring.
* Snapshot isolation behaves differently because it uses row versioning instead of traditional locking.
