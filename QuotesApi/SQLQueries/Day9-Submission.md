# Day 9 — Reproduce and Resolve a Deadlock

---

## Objective

Reproduce a classic two-resource deadlock using two concurrent SQL Server sessions on the `Quotes` table, prove the deadlock occurred using the Extended Events deadlock graph, and eliminate the deadlock permanently by enforcing consistent lock acquisition order across all sessions.

---

## Environment

| Property | Value |
|---|---|
| Server | `pratiksha-sql-server-01` |
| Database | `quotes-sql-db` (dbid = 5) |
| Table | `dbo.Quotes` |
| Index | `PK_Quotes` (hobtid = `72057594049069056`) |
| Client | VS Code — `vscode-mssql-Query` |
| Host | `PRATIKSHA` (hostpid = 6752) |
| Login | `pratiksha-quotesdb` |
| Isolation level | READ COMMITTED (2) |
| XE session | `DeadlockCapture` |

---

## Deadlock Reproduction

Two query windows were opened simultaneously against `quotes-sql-db`. Each window was started within the same 5-second window to guarantee timing overlap.

### Session 1 — `deadlock-session1.sql`

Locks `Id = 1` first, then attempts to acquire `Id = 2`.

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET Text = 'Locked By Session 1'
WHERE Id = 1;

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET Text = 'Session 1 Wants Row 2'
WHERE Id = 2;

ROLLBACK TRANSACTION;
```

### Session 2 — `deadlock-session2.sql`

Locks `Id = 2` first, then attempts to acquire `Id = 1`.

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET Text = 'Locked By Session 2'
WHERE Id = 2;

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET Text = 'Session 2 Wants Row 1'
WHERE Id = 1;
```

> **Note on `WAITFOR DELAY`:** The delay widens the timing window so that both sessions complete their first `UPDATE` (acquiring an exclusive lock) before either attempts its second `UPDATE`. This makes the circular wait condition reliably reproducible. `WAITFOR DELAY` is a reproduction aid — it plays no role in preventing or causing deadlocks in production.

---

## Deadlock Victim Output

When Session 1 was elected victim, VS Code displayed the following error in the Query Results panel (`DeadLock-Victim.png`):

```
Msg 1205, Level 13, State 72, Line 1
Transaction (Process ID 51) was deadlocked on lock resources
with another process and has been chosen as the deadlock victim.
Rerun the transaction.

Total execution time: 00:00:02.547
```

**Victim: SPID 51 (Session 1).** The 2.547 second execution time confirms Session 1 was killed before the 5-second `WAITFOR DELAY` completed — SQL Server interrupted it mid-wait upon detecting the circular dependency.

---

## Extended Events Configuration

The deadlock graph was captured using an Extended Events session named `DeadlockCapture` running on the `quotes-sql-db` database. The session listens for the `database_xml_deadlock_report` event and stores output in a ring buffer target.

> **Note:** No `CREATE EVENT SESSION` script was committed to this project. The session was created interactively via SSMS or the Azure Portal prior to running the reproduction scripts. The session name `DeadlockCapture` is confirmed by `Test.sql`.

After the deadlock occurred, the graph XML was extracted from the ring buffer using `Test.sql`:

```sql
SELECT
    CAST(target_data AS XML)
FROM sys.dm_xe_database_session_targets t
JOIN sys.dm_xe_database_sessions s
    ON t.event_session_address = s.address
WHERE s.name = 'DeadlockCapture';
```

The output was saved as `DeadLock-Graph.xml`. The ring buffer recorded **two separate deadlock events** (`eventCount="2"`, `droppedCount="0"`):

| Event | Timestamp | deadlock_cycle_id | Victim SPID |
|---|---|---|---|
| 1 | `2026-06-05T06:00:16.960Z` | 625 | 51 |
| 2 | `2026-06-05T06:11:30.417Z` | 760 | 51 |

SPID 51 was elected victim in both independent runs.

---

## Deadlock Graph Evidence

All values below are extracted verbatim from `DeadLock-Graph.xml`, event 1 (cycle 625).

### Victim declaration

```xml
<victim-list>
    <victimProcess id="process237f7a9f048"/>
</victim-list>
```

`process237f7a9f048` = **SPID 51** — Session 1 was killed by the deadlock monitor.

---

### Process list

#### SPID 51 — Session 1 (deadlock victim)

```
process id     = process237f7a9f048
spid           = 51
status         = suspended
waitresource   = XACT: 5:14663:0  KEY: 5:72057594049069056 (61a06abd401c)
waittime       = 5880 ms
lockMode       = S
ownerId        = 61083
trancount      = 2
lasttranstarted = 2026-06-05T06:00:06.067
```

Input buffer captured inside the graph:

```sql
BEGIN TRANSACTION;
UPDATE Quotes SET Text = 'Locked By Session 1' WHERE Id = 1;
WAITFOR DELAY '00:00:05';
UPDATE Quotes SET Text = 'Session 1 Wants Row 2' WHERE Id = 2;   ← blocked here
```

`trancount = 2` confirms Session 1 had already executed and committed its first `UPDATE` (locking `Id = 1`) before blocking on `Id = 2`.

---

#### SPID 62 — Session 2 (survivor)

```
process id     = process237ef7c7c18
spid           = 62
status         = suspended
waitresource   = XACT: 5:14660:0  KEY: 5:72057594049069056 (8194443284a0)
waittime       = 2097 ms
lockMode       = S
ownerId        = 61097
trancount      = 2
lasttranstarted = 2026-06-05T06:00:09.860
```

Input buffer captured inside the graph:

```sql
BEGIN TRANSACTION;
UPDATE Quotes SET Text = 'Locked By Session 2' WHERE Id = 2;
WAITFOR DELAY '00:00:05';
UPDATE Quotes SET Text = 'Session 2 Wants Row 1' WHERE Id = 1;   ← blocked here
```

---

### Resource list — the circular hold-and-wait

Two `xactlock` entries in the `<resource-list>` section define the cycle completely.

#### Lock A — `Quotes.Id = 2` (key hash `61a06abd401c`)

```xml
<xactlock xdesIdLow="14663" xdesIdHigh="0" dbid="5"
          id="lock237e6e8d300" mode="X">
    <UnderlyingResource>
        <keylock hobtid="72057594049069056" dbid="5"
                 objectname="...dbo.Quotes" indexname="PK_Quotes"/>
    </UnderlyingResource>
    <owner-list>
        <owner id="process237ef7c7c18" mode="X"/>    <!-- SPID 62 owns X lock -->
    </owner-list>
    <waiter-list>
        <waiter id="process237f7a9f048" mode="S" requestType="wait"/>  <!-- SPID 51 waiting -->
    </waiter-list>
</xactlock>
```

**SPID 62 holds an exclusive (X) lock on `Quotes.Id = 2`. SPID 51 is waiting for it.**

---

#### Lock B — `Quotes.Id = 1` (key hash `8194443284a0`)

```xml
<xactlock xdesIdLow="14660" xdesIdHigh="0" dbid="5"
          id="lock237f6c00980" mode="X">
    <UnderlyingResource>
        <keylock hobtid="72057594049069056" dbid="5"
                 objectname="...dbo.Quotes" indexname="PK_Quotes"/>
    </UnderlyingResource>
    <owner-list>
        <owner id="process237f7a9f048" mode="X"/>    <!-- SPID 51 owns X lock -->
    </owner-list>
    <waiter-list>
        <waiter id="process237ef7c7c18" mode="S" requestType="wait"/>  <!-- SPID 62 waiting -->
    </waiter-list>
</xactlock>
```

**SPID 51 holds an exclusive (X) lock on `Quotes.Id = 1`. SPID 62 is waiting for it.**

---

### How the deadlock graph proves the deadlock occurred

The `database_xml_deadlock_report` event is emitted by SQL Server's internal deadlock monitor — a background thread that walks the lock manager's waiter graph every ~5 seconds looking for cycles. The monitor only fires this event when it has **mathematically confirmed** a circular wait and has already selected and rolled back a victim. Three elements in the XML together constitute definitive proof:

1. **`<victimProcess id="process237f7a9f048"/>`** — a specific process handle is named as victim. This element is never populated by a waiting session; SQL Server writes it at the instant the cycle is broken.

2. **`<resource-list>` with two interlocking `<xactlock>` entries** — each entry shows an owner and a waiter. The owner of Lock A (SPID 62) is the waiter of Lock B, and the owner of Lock B (SPID 51) is the waiter of Lock A. This is the definition of a cycle: no amount of waiting will ever resolve it without an external intervention.

3. **`trancount = 2` on both processes** — both sessions had already incremented their transaction nesting count by executing their first `UPDATE`, meaning each session was genuinely holding a real exclusive lock when it blocked.

The same structural pattern was independently captured twice (cycle IDs 625 and 760), confirming reproducibility, not coincidence.

---

## Deadlock Diagram

Derived directly from `DeadLock-Graph.xml`, cycle 625 (2026-06-05T06:00:16.960Z):

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          CIRCULAR WAIT — DEADLOCK                        │
│                                                                          │
│   SPID 51 — Session 1 (VICTIM)        SPID 62 — Session 2               │
│   process237f7a9f048                  process237ef7c7c18                 │
│   ownerId = 61083                     ownerId = 61097                    │
│   started 06:00:06.067                started 06:00:09.860               │
│                                                                          │
│   HOLDS exclusive (X) lock on         HOLDS exclusive (X) lock on        │
│   ┌──────────────────────┐            ┌──────────────────────┐           │
│   │  Quotes.Id = 1       │            │  Quotes.Id = 2       │           │
│   │  key: 8194443284a0   │            │  key: 61a06abd401c   │           │
│   │  xdesIdLow = 14660   │            │  xdesIdLow = 14663   │           │
│   └──────────────────────┘            └──────────────────────┘           │
│            │                                       │                     │
│            │  WAITING (5880 ms) for ───────────────┘                     │
│            │  Quotes.Id = 2                                               │
│            │                                                              │
│            └─────────────── WAITING (2097 ms) for                        │
│                             Quotes.Id = 1  ◄──────────────────────────   │
│                                                                           │
│  Neither session can proceed. Neither releases its held lock.             │
│  SQL Server deadlock monitor fires, elects SPID 51 as victim.            │
│  SPID 51 receives Msg 1205 and is rolled back.                           │
│  SPID 62 unblocks and completes.                                         │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## Root Cause Analysis

The deadlock is caused by **inverted lock acquisition order** between two concurrent transactions that need the same set of rows.

| Step | Time | SPID 51 (Session 1) | SPID 62 (Session 2) |
|---|---|---|---|
| 1 | T+0s | Acquires **X lock on `Id=1`** | — |
| 2 | T+3.8s | Enters `WAITFOR DELAY` | Acquires **X lock on `Id=2`** |
| 3 | T+3.8s | — | Enters `WAITFOR DELAY` |
| 4 | T+8.8s | Tries to acquire X lock on `Id=2` → **BLOCKED** (SPID 62 holds it) | — |
| 5 | T+8.8s | — | Tries to acquire X lock on `Id=1` → **BLOCKED** (SPID 51 holds it) |
| 6 | T+10.9s | **KILLED** by deadlock monitor — Msg 1205 | Unblocks, completes |

All four Coffman conditions for deadlock are satisfied:

| Condition | How it applies |
|---|---|
| **Mutual exclusion** | `UPDATE` acquires exclusive (X) locks; X locks cannot be shared |
| **Hold and wait** | Each session holds its first lock while blocked on its second |
| **No preemption** | SQL Server cannot forcibly transfer a lock; it must kill a transaction |
| **Circular wait** | SPID 51 waits for SPID 62, and SPID 62 waits for SPID 51 |

The circular wait is the critical condition. Eliminate it and the other three conditions become harmless.

---

## Fix

The fix reverses Session 2's lock acquisition order so that it matches Session 1. Both sessions now request resources in the order `Id = 1` → `Id = 2`.

### Fix — Session 1 (`Deadlock-Fix-Session1.png`)

Lock order is unchanged: `Id = 1` first, `Id = 2` second. `COMMIT` replaces `ROLLBACK`.

```sql
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Session 1 Row 1'
WHERE Id = 1;          -- acquires X lock on Id=1 first

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET QuoteText = 'Session 1 Row 2'
WHERE Id = 2;          -- acquires X lock on Id=2 second

COMMIT;
```

### Fix — Session 2 (`Deadlock-Fix-Session2.png`, marked `--Fixed Script`)

**Critical change:** the first `UPDATE` now targets `Id = 1` instead of `Id = 2`. Both sessions now acquire locks in the same order.

```sql
--Fixed Script
BEGIN TRANSACTION;

UPDATE Quotes
SET QuoteText = 'Session 2 Row 1'
WHERE Id = 1;          -- acquires X lock on Id=1 first  ← ORDER CHANGED

WAITFOR DELAY '00:00:05';

UPDATE Quotes
SET QuoteText = 'Session 2 Row 2'
WHERE Id = 2;          -- acquires X lock on Id=2 second

COMMIT;
```

### Fix verification

The fix was run concurrently. `Fixed-Deadlock.png` shows the result:

```
Started executing query at Line 16
(1 row affected)
(1 row affected)
Total execution time: 00:00:05.013
```

No Msg 1205. No rollback. Both rows updated successfully in ~5 seconds (the duration of `WAITFOR DELAY`). The two sessions ran simultaneously without deadlocking.

### Is the fix correct?

**Yes.** Both sessions now acquire `Id = 1` before `Id = 2`. The circular wait cannot form:

| | Session 1 | Session 2 |
|---|---|---|
| First lock acquired | `Quotes.Id = 1` | `Quotes.Id = 1` |
| Second lock acquired | `Quotes.Id = 2` | `Quotes.Id = 2` |
| Lock order | ✅ Id=1 → Id=2 | ✅ Id=1 → Id=2 |

> `WAITFOR DELAY` is preserved in the fix scripts solely to prove that two sessions can overlap for 5 seconds with the same lock ordering and still not deadlock. It is not the mechanism that prevents the deadlock.

---

## Why the Fix Works

### Why consistent lock ordering prevents deadlocks

A deadlock requires a circular wait. A circular wait requires two sessions to each hold a resource that the other needs — which only becomes possible when they request resources in **opposite orders**.

With consistent ordering (`Id = 1` always before `Id = 2`), the following scenario plays out:

```
Session 1 acquires X lock on Id=1  ✓
Session 2 tries to acquire X lock on Id=1  → BLOCKS, waits for Session 1
Session 1 acquires X lock on Id=2  ✓  (no contention — Session 2 never reached Id=2)
Session 1 COMMITs, releases both locks
Session 2 unblocks, acquires Id=1, then Id=2, COMMITs
```

Session 2 is blocked at the **first** lock, before it ever touches `Id = 2`. The circular hold-and-wait cannot form because at no point do both sessions simultaneously hold a lock that the other needs.

This is a total resource ordering guarantee: if every transaction requesting resources R1 and R2 always acquires R1 before R2, the directed graph of "who waits for whom" can never contain a cycle. This is a direct application of Coffman's 1971 result — breaking circular wait makes deadlock structurally impossible regardless of timing, transaction count, or concurrency level.

`WAITFOR DELAY` cannot prevent a deadlock because it has no effect on lock acquisition order. Two sessions that both delay 5 seconds but still lock in opposite order will deadlock just as reliably — the delay only changes when they block, not whether they block in a cycle.

---

## What I Learned

- A deadlock is a **circular wait**, not simply a long wait. A slow query that eventually finishes is not a deadlock. A deadlock requires two sessions to each hold a lock the other needs, with neither able to proceed.

- The **Extended Events `database_xml_deadlock_report`** event is authoritative proof of a deadlock. It is emitted by SQL Server's deadlock monitor at the exact moment the cycle is confirmed and a victim is selected. The `<victim-list>`, `<resource-list>` owner/waiter pairs, and `trancount` values together make the circular dependency unambiguous.

- **SPID 51 was the victim in both captured runs** (cycle IDs 625 and 760). SQL Server selects the least expensive transaction to roll back — in both cases, Session 1 had consumed the same log (`logused = 812`) as Session 2 and was chosen deterministically.

- **`WAITFOR DELAY` is a timing tool, not a fix.** It widens the concurrency window so a deadlock manifests reliably during testing. Removing it from production scripts reduces the deadlock window but does not eliminate the root cause — only consistent lock ordering does that.

- **Consistent lock ordering eliminates the circular wait condition.** When every transaction that needs rows R1 and R2 always acquires R1 before R2, the hold-and-wait cycle that defines a deadlock can never form. This requires application-level discipline, not a database setting.

- **`trancount = 2`** in both process nodes of the graph confirms that each session had already executed its first `UPDATE` (incrementing the transaction nesting count) before blocking on its second — meaning genuine exclusive locks were being held on both sides simultaneously.

- Deadlocks in production are almost always caused by inconsistent lock ordering across different code paths that happen to touch the same rows. The fix — enforcing a canonical ordering — should be applied at the application or stored-procedure level to ensure every caller follows the same sequence.
