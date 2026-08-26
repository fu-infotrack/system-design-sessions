# PostgreSQL Advisory Locks and Connection Pooling (PgBouncer)

## Summary

PostgreSQL advisory locks are application-defined heavyweight locks with two scopes: session-scoped (`pg_advisory_lock`), held until explicitly unlocked or the session ends and *unaffected by transaction rollback*, and transaction-scoped (`pg_advisory_xact_lock`), released automatically at end of transaction with no manual unlock. The one-`bigint` and two-`int4` key forms occupy **genuinely separate lock spaces** — the source stores a discriminator (`1` vs `2`) in the fourth locktag field, so `pg_advisory_lock(0::bigint)` and `pg_advisory_lock(0, 0)` never collide; but *within* a form, hashing strings into keys collides silently and serialises unrelated work. Under PgBouncer **transaction** (and **statement**) pooling, session-scoped advisory locks are documented as "Never" supported, and the failure mode is not an error — the acquisition succeeds, the server connection is returned to the pool **still holding the lock**, `server_reset_query` (`DISCARD ALL`) is **not run in transaction mode by default**, and a later unrelated client that lands on that same backend gets a spurious `true` from `pg_try_advisory_lock` for a key it does not own. `pg_advisory_xact_lock` is safe in every pooling mode because its lifetime is bounded by the transaction, which is exactly PgBouncer's checkout unit — and so is `SELECT ... FOR UPDATE`. Contrary to common belief, both `lock_timeout` and `statement_timeout` **do** apply to advisory lock waits (verified empirically; advisory waits go through the same `WaitOnLock`/`ProcSleep` path as every other heavyweight lock).

> **Verification note.** Every empirical claim below was reproduced locally on **PostgreSQL 17.11** (Docker `postgres:17`) fronted by **PgBouncer 1.25.2** (`edoburu/pgbouncer`). Raw outputs are reproduced inline. Primary doc sources are cited per claim; `/docs/current/` was PostgreSQL **18** at time of writing, and the section numbering (§13.3.5 Advisory Locks, §9.28.10 Advisory Lock Functions, Table 9.109) is that of the current docs.

---

## Findings

### 1. The two scopes

#### 1.1 What the docs say (verbatim)

From the Explicit Locking chapter, **§13.3.5 Advisory Locks** — <https://www.postgresql.org/docs/current/explicit-locking.html>:

> "There are two ways to acquire an advisory lock in PostgreSQL: at session level or at transaction level. Once acquired at session level, an advisory lock is held until explicitly released or the session ends. Unlike standard lock requests, session-level advisory lock requests do not honor transaction semantics: a lock acquired during a transaction that is later rolled back will still be held following the rollback, and likewise an unlock is effective even if the calling transaction fails later. **A lock can be acquired multiple times by its owning process; for each completed lock request there must be a corresponding unlock request before the lock is actually released.** Transaction-level lock requests, on the other hand, behave more like regular lock requests: they are automatically released at the end of the transaction, and there is no explicit unlock operation. This behavior is often more convenient than the session-level behavior for short-term usage of an advisory lock. Session-level and transaction-level lock requests for the same advisory lock identifier will block each other in the expected way. **If a session already holds a given advisory lock, additional requests by it will always succeed, even if other sessions are awaiting the lock**; this statement is true regardless of whether the existing lock hold and new request are at session level or transaction level."

And from **§9.28.10 Advisory Lock Functions** — <https://www.postgresql.org/docs/current/functions-admin.html>:

> "Locks can be taken at session level (so that they are held until released or the session ends) or at transaction level (so that they are held until the current transaction ends; there is no provision for manual release). **Multiple session-level lock requests stack, so that if the same resource identifier is locked three times there must then be three unlock requests to release the resource in advance of session end.**"

#### 1.2 Why, in the source

`src/backend/utils/adt/lockfuncs.c` — the *only* difference between the session and transaction variants is the third argument to `LockAcquire`, `sessionLock`:

```c
/* pg_advisory_lock(int8) - acquire exclusive lock on an int8 key */
Datum
pg_advisory_lock_int8(PG_FUNCTION_ARGS)
{
    int64       key = PG_GETARG_INT64(0);
    LOCKTAG     tag;
    SET_LOCKTAG_INT64(tag, key);
    (void) LockAcquire(&tag, ExclusiveLock, true, false);   /* sessionLock = true  */
    PG_RETURN_VOID();
}

/* pg_advisory_xact_lock(int8) */
Datum
pg_advisory_xact_lock_int8(PG_FUNCTION_ARGS)
{
    ...
    (void) LockAcquire(&tag, ExclusiveLock, false, false);  /* sessionLock = false */
    PG_RETURN_VOID();
}
```

Source: <https://github.com/postgres/postgres/blob/master/src/backend/utils/adt/lockfuncs.c>

`LockAcquire`'s contract (`src/backend/storage/lmgr/lock.c`):

```
 *  sessionLock: if true, acquire lock for session not current transaction
 *  dontWait: if true, don't wait to acquire lock
 ...
 *      LOCKACQUIRE_ALREADY_HELD    incremented count for lock already held
```

The stacking is a per-owner counter in the `LOCALLOCK`, `src/include/storage/lock.h`:

```c
typedef struct LOCALLOCKOWNER
{
    /*
     * Note: if owner is NULL then the lock is held on behalf of the session;
     * otherwise it is held on behalf of my current transaction.
     */
    struct ResourceOwnerData *owner;
    int64       nLocks;         /* # of times held by this owner */
} LOCALLOCKOWNER;
```

So "session-scoped" literally means *owner == NULL* — no resource owner, therefore nothing at transaction end releases it. That is the whole mechanism, and it is exactly why PgBouncer's transaction boundary cannot clean it up.

Source: <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/include/storage/lock.h>

#### 1.3 Function signatures (Table 9.109, current docs)

<https://www.postgresql.org/docs/current/functions-admin.html>

| Function | Description (verbatim) |
|---|---|
| `pg_advisory_lock(key bigint) → void`<br>`pg_advisory_lock(key1 integer, key2 integer) → void` | Obtains an exclusive session-level advisory lock, waiting if necessary. |
| `pg_advisory_lock_shared(...)` → `void` | Obtains a shared session-level advisory lock, waiting if necessary. |
| `pg_advisory_unlock(...)` → `boolean` | Releases a previously-acquired exclusive session-level advisory lock. Returns `true` if the lock is successfully released. If the lock was not held, `false` is returned, and in addition, an SQL warning will be reported by the server. |
| `pg_advisory_unlock_shared(...)` → `boolean` | Releases a previously-acquired shared session-level advisory lock. (Same `true`/`false`+warning semantics.) |
| `pg_advisory_unlock_all() → void` | Releases all session-level advisory locks held by the current session. **(This function is implicitly invoked at session end, even if the client disconnects ungracefully.)** |
| `pg_advisory_xact_lock(...)` → `void` | Obtains an exclusive transaction-level advisory lock, waiting if necessary. |
| `pg_advisory_xact_lock_shared(...)` → `void` | Obtains a shared transaction-level advisory lock, waiting if necessary. |
| `pg_try_advisory_lock(...)` → `boolean` | Obtains an exclusive session-level advisory lock if available. This will either obtain the lock immediately and return `true`, or return `false` without waiting if the lock cannot be acquired immediately. |
| `pg_try_advisory_lock_shared(...)` → `boolean` | Shared variant of the above. |
| `pg_try_advisory_xact_lock(...)` → `boolean` | Obtains an exclusive transaction-level advisory lock if available. Immediate `true`, or `false` without waiting. |
| `pg_try_advisory_xact_lock_shared(...)` → `boolean` | Shared variant of the above. |

Note the asymmetry worth calling out on a slide: **there is no `pg_advisory_xact_unlock`.** Transaction-level locks have "no provision for manual release" (docs, §9.28.10).

#### 1.4 Empirically confirmed semantics (PostgreSQL 17.11)

**Stacking / unlock counting — CONFIRMED.** Three acquisitions produce **one** row in `pg_locks` and require **three** unlocks:

```
SELECT pg_advisory_lock(42);  x3
 pg_locks_rows | 1
 unlock1       | t     still_there | 1     <- STILL HELD after one unlock
 unlock2       | t
 unlock3       | t     after_3_unlocks | 0
 unlock4       -> WARNING:  you don't own a lock of type ExclusiveLock
               -> f
```

The over-unlock returns `false` **and** emits `WARNING: you don't own a lock of type ExclusiveLock` — it does **not** raise an error, so a naive `unlock` in a `finally` block fails silently. This matches the documented "`false` is returned, and in addition, an SQL warning will be reported by the server."

**Rollback semantics — CONFIRMED.**

```sql
BEGIN;
  SELECT pg_advisory_lock(100);       -- session-scoped
  SELECT pg_advisory_xact_lock(200);  -- xact-scoped
ROLLBACK;
SELECT objid FROM pg_locks WHERE locktype='advisory';
-- objid | objsubid | note
--   100 |        1 | held after ROLLBACK      <- session lock SURVIVED the rollback
-- (200 is gone)
```

**Unlock inside a rolled-back transaction is still effective — CONFIRMED.**

```sql
SELECT pg_advisory_lock(300);
BEGIN; SELECT pg_advisory_unlock(300); ROLLBACK;
SELECT count(*) FROM pg_locks WHERE objid=300;  --> 0
```

**Savepoint behaviour (not in the docs; measured).** A *transaction-level* advisory lock taken inside a savepoint **is released by `ROLLBACK TO SAVEPOINT`**:

```sql
BEGIN; SAVEPOINT s1;
  SELECT pg_advisory_xact_lock(400);
  -- inside: 1 row in pg_locks
ROLLBACK TO SAVEPOINT s1;
  -- after_rollback_to_savepoint: 0
```

For calibration, an ordinary `LOCK TABLE t IN ACCESS EXCLUSIVE MODE` inside the same savepoint behaves **identically** (also released). So this is general resource-owner behaviour, not an advisory-lock quirk — but it does contradict the folk rule "locks are always held to end of transaction". *(Measured on 17.11; not stated in the docs I found — see Unverified / open.)*

**Shared vs exclusive — CONFIRMED.** In `pg_locks` the modes render as `ShareLock` and `ExclusiveLock`. Cross-mode unlock fails:

```
SELECT pg_advisory_lock_shared(555);
SELECT pg_advisory_unlock(555);        -> WARNING: you don't own a lock of type ExclusiveLock; f
SELECT pg_advisory_unlock_shared(555); -> t
```

And a shared lock is genuinely shared across sessions:

```
-- session 1 holds pg_advisory_lock_shared(600)
-- session 2:
 other_session_shared | other_session_exclusive
 t                    | f
```

**Mixed session + transaction hold on the same key by the same session — CONFIRMED.** Still one `pg_locks` row; the transaction hold evaporates at `COMMIT`, and one `pg_advisory_unlock` then clears the session hold (the counters are per-owner, per §1.2).

**Deadlock detection applies to advisory locks — CONFIRMED.** Two sessions taking `pg_advisory_xact_lock(1001)`/`(1002)` in opposite order:

```
ERROR:  deadlock detected
DETAIL:  Process 90 waits for ExclusiveLock on advisory lock [5,0,1001,1]; blocked by process 89.
         Process 89 waits for ExclusiveLock on advisory lock [5,0,1002,1]; blocked by process 90.
```

Note the locktag rendering `[database, classid, objid, objsubid]` — useful when reading production logs.

#### 1.5 Two documented footguns worth a slide

**Shared memory pool.** From §13.3.5:

> "Both advisory locks and regular locks are stored in a shared memory pool whose size is defined by the configuration variables `max_locks_per_transaction` and `max_connections`. Care must be taken not to exhaust this memory or the server will be unable to grant any locks at all. This imposes an upper limit on the number of advisory locks grantable by the server, typically in the tens to hundreds of thousands depending on how the server is configured."

Confirmed by measurement that advisory locks are **never** fast-path (`fastpath = f` in `pg_locks`), so they always consume a main lock-table entry.

**Evaluation-order trap.** Also from §13.3.5, verbatim:

```sql
SELECT pg_advisory_lock(id) FROM foo WHERE id = 12345; -- ok
SELECT pg_advisory_lock(id) FROM foo WHERE id > 12345 LIMIT 100; -- danger!
SELECT pg_advisory_lock(q.id) FROM
(
  SELECT id FROM foo WHERE id > 12345 LIMIT 100
) q; -- ok
```

> "In the above queries, the second form is dangerous because the `LIMIT` is not guaranteed to be applied before the locking function is executed. This might cause some locks to be acquired that the application was not expecting, and hence would fail to release (until it ends the session). From the point of view of the application, such locks would be dangling, although still viewable in `pg_locks`."

Source for both: <https://www.postgresql.org/docs/current/explicit-locking.html>

---

### 2. The key space

#### 2.1 The two forms do NOT share a lock space — confirmed three ways

**(a) Docs, verbatim** — <https://www.postgresql.org/docs/current/functions-admin.html>, §9.28.10:

> "All these functions are intended to be used to lock application-defined resources, which can be identified either by a single 64-bit key value or two 32-bit key values **(note that these two key spaces do not overlap)**."

**(b) Source** — `src/backend/utils/adt/lockfuncs.c`:

```c
/*
 * Functions for manipulating advisory locks
 *
 * We make use of the locktag fields as follows:
 *
 *  field1: MyDatabaseId ... ensures locks are local to each database
 *  field2: first of 2 int4 keys, or high-order half of an int8 key
 *  field3: second of 2 int4 keys, or low-order half of an int8 key
 *  field4: 1 if using an int8 key, 2 if using 2 int4 keys
 */
#define SET_LOCKTAG_INT64(tag, key64) \
    SET_LOCKTAG_ADVISORY(tag, \
                         MyDatabaseId, \
                         (uint32) ((key64) >> 32), \
                         (uint32) (key64), \
                         1)
#define SET_LOCKTAG_INT32(tag, key1, key2) \
    SET_LOCKTAG_ADVISORY(tag, MyDatabaseId, key1, key2, 2)
```

`field4` is a **discriminator baked into the lock tag**, so the two forms can never hash to the same lock object even with byte-identical key material. Source: <https://github.com/postgres/postgres/blob/master/src/backend/utils/adt/lockfuncs.c>

The underlying macro, `src/include/storage/lock.h`:

```c
#define SET_LOCKTAG_ADVISORY(locktag,id1,id2,id3,id4) \
    ((locktag).locktag_field1 = (id1), \
     (locktag).locktag_field2 = (id2), \
     (locktag).locktag_field3 = (id3), \
     (locktag).locktag_field4 = (id4), \
     (locktag).locktag_type = LOCKTAG_ADVISORY, \
     (locktag).locktag_lockmethodid = USER_LOCKMETHOD)
```

Two further details visible here and worth mentioning:
- `locktag_field1 = MyDatabaseId` — **advisory locks are per-database**, not cluster-wide. Two databases in the same cluster do not contend.
- `locktag_lockmethodid = USER_LOCKMETHOD`, not `DEFAULT_LOCKMETHOD` — a separate lock method (`lock.c` defines `user_lockmethod` alongside `default_lockmethod`), though it shares the same lock modes and conflict table, and the same `WaitOnLock`/`ProcSleep` wait path.

Source: <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/include/storage/lock.h>, <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/storage/lmgr/lock.c>

**(c) Measured** — the decisive experiment. Same numeric key material, both succeed, two distinct lock objects:

```
SELECT pg_advisory_lock(0::bigint);   -- succeeds
SELECT pg_try_advisory_lock(0, 0);    -- ALSO succeeds -> t

 locktype | database | classid | objid | objsubid |     mode      | granted
----------+----------+---------+-------+----------+---------------+---------
 advisory |        5 |       0 |     0 |        1 | ExclusiveLock | t     <- bigint form
 advisory |        5 |       0 |     0 |        2 | ExclusiveLock | t     <- (int4,int4) form
```

And likewise `pg_advisory_lock(1::bigint)` vs `pg_advisory_lock(0, 1)` — identical `classid`/`objid` (`0`/`1`), differing only in `objsubid` (`1` vs `2`). **They do not collide.**

#### 2.2 Overload resolution

```
 proname          | args
------------------+------------------
 pg_advisory_lock | bigint
 pg_advisory_lock | integer, integer
```

There is no one-argument `integer` overload, so `pg_advisory_lock(42)` unambiguously resolves to the `bigint` form. The forms are distinguished purely by arity — you cannot accidentally hop between key spaces, but you also cannot deliberately migrate a key from one to the other without changing the lock identity.

#### 2.3 Collision behaviour *within* a form — the real risk

The docs are silent on this; it is a consequence of the design. Advisory lock keys are **opaque integers with no namespacing**. Every part of your system that hashes a string into the same 64-bit space shares one flat namespace, cluster-wide-per-database, including keys chosen by libraries you did not write (Rails' `with_advisory_lock`, Flyway/Liquibase migration locks, Quartz-style schedulers, `good_job`, Sidekiq-alikes). A collision does not error — it **silently serialises two unrelated workloads**, and it looks exactly like a slow query.

The common idiom is the worst offender:

```sql
SELECT pg_advisory_lock(hashtext('order:1001'));
```

`hashtext()` returns **`integer`, not `bigint`** (measured: `pg_typeof(hashtext('abc')) = integer`). So this uses only **2^32** of the available 2^64 key space. By the birthday bound you reach a 50% chance of at least one collision at roughly `1.18 * sqrt(2^32)` ≈ **77,000 distinct keys** — and a ~1-in-a-million collision chance at only ~93 keys. For a job queue keyed per order ID that is not a theoretical risk.

Safer derivations:
- `hashtextextended(k, 0)` → returns `bigint` (measured), full 64-bit space.
- `('x' || substr(md5(k), 1, 16))::bit(64)::bigint` → full 64-bit, and stable across PostgreSQL versions (`hashtext` is explicitly *not* guaranteed stable across versions/platforms).
- Or use the **two-`int4` form deliberately as a namespace**: `pg_advisory_xact_lock(<app_or_table_id>, <row_id>)`. This is the single most under-used feature here — `classid` becomes a human-readable tag you assign, and `pg_locks` then tells you *which subsystem* holds the lock, not just an opaque number.

---

### 3. Where advisory locks show up: `pg_locks`

Column definitions — <https://www.postgresql.org/docs/current/view-pg-locks.html>. `locktype` for advisory locks is the literal string **`advisory`** (the full enumeration is `relation`, `extend`, `frozenid`, `page`, `tuple`, `transactionid`, `virtualxid`, `spectoken`, `object`, `userlock`, `advisory`, `applytransaction`).

The mapping is set in `pg_lock_status()`, `src/backend/utils/adt/lockfuncs.c`:

```c
case LOCKTAG_ADVISORY:
default:            /* treat unknown locktags like OBJECT */
    values[1] = ObjectIdGetDatum(instance->locktag.locktag_field1);  /* database  */
    values[7] = ObjectIdGetDatum(instance->locktag.locktag_field2);  /* classid   */
    values[8] = ObjectIdGetDatum(instance->locktag.locktag_field3);  /* objid     */
    values[9] = Int16GetDatum(instance->locktag.locktag_field4);     /* objsubid  */
    nulls[2] = true;  /* relation           */
    nulls[3] = true;  /* page               */
    nulls[4] = true;  /* tuple              */
    nulls[5] = true;  /* virtualxid         */
    nulls[6] = true;  /* transactionid      */
    break;
```

So, for an advisory lock:

| Column | Value |
|---|---|
| `locktype` | `advisory` |
| `database` | the database OID (advisory locks are per-database) |
| `relation`, `page`, `tuple`, `virtualxid`, `transactionid` | all **NULL** |
| `classid` | high-order 32 bits of the `bigint` key, **or** `key1` of the two-int form |
| `objid` | low-order 32 bits of the `bigint` key, **or** `key2` of the two-int form |
| `objsubid` | **`1`** = single `bigint` key; **`2`** = two `int4` keys |
| `mode` | `ExclusiveLock` or `ShareLock` |
| `granted` | `f` while waiting |
| `fastpath` | always `f` for advisory locks (measured) |
| `waitstart` | when the wait began, if `granted = f` |

**Crucially, `pg_locks` does not distinguish session-scoped from transaction-scoped advisory locks.** There is no column for it. You infer it: a transaction-scoped lock always has a live transaction behind it, so a row whose `pid` has `pg_stat_activity.state = 'idle'` (not `idle in transaction`) is a **session-scoped lock**, and that is precisely the shape of a pooled leak.

Reassembling the `bigint` key (both `classid` and `objid` are `oid`, i.e. unsigned 32-bit — cast through `bigint` before shifting):

```sql
SELECT (classid::bigint << 32) | objid::bigint AS key
FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1;
```

---

### 4. PgBouncer — the critical part

#### 4.1 What PgBouncer documents, verbatim

The pooling modes, from <https://www.pgbouncer.org/features.html>:

> **Session pooling** — "Most polite method. When a client connects, a server connection will be assigned to it for the whole duration it stays connected. When the client disconnects, the server connection will be put back into pool. **This mode supports all PostgreSQL features.**"
>
> **Transaction pooling** — "A server connection is assigned to a client only during a transaction. When PgBouncer notices that the transaction is over, the server will be put back into the pool. **This mode breaks a few session-based features of PostgreSQL. You can use it only when the application cooperates by not using features that break.** See the table below for incompatible features."
>
> **Statement pooling** — "Most aggressive method. This is transaction pooling with a twist: Multi-statement transactions are disallowed. This is meant to enforce "autocommit" mode on the client, mostly targeted at PL/Proxy."

The feature matrix, headed **"SQL feature map for pooling modes"**, with its verbatim preamble:

> "The following table list various PostgreSQL features and whether they are compatible with PgBouncer pooling modes. Note that "transaction" pooling breaks client expectations of the server by design and can be used only if the application cooperates by not using non-working features."

| Feature | Session pooling | Transaction pooling |
|---|---|---|
| Startup parameters ¹ | Yes | Yes |
| SET/RESET | Yes | Never |
| LISTEN | Yes | Never |
| NOTIFY | Yes | Yes |
| WITHOUT HOLD CURSOR | Yes | Yes |
| WITH HOLD CURSOR | Yes | Never |
| Protocol-level prepared plans | Yes | Yes ² |
| PREPARE / DEALLOCATE | Yes | Never |
| ON COMMIT DROP temp tables | Yes | Yes |
| PRESERVE/DELETE ROWS temp tables | Yes | Never |
| Cached plan reset | Yes | Yes |
| LOAD statement | Yes | Never |
| **Session-level advisory locks** | **Yes** | **Never** |

> **Correction worth making on stage:** the table as published (checked against the raw HTML of pgbouncer.org/features.html, PgBouncer 1.25.2 era) has **only two mode columns — Session and Transaction. There is no Statement column.** Several summaries (including AI ones) invent a third column. Statement mode's status follows from the prose instead: it is defined as "transaction pooling with a twist", so everything marked "Never" under transaction pooling is also unavailable under statement pooling, plus multi-statement transactions.
>
> Also note the row says **"Session-level advisory locks"**, not "advisory locks". PgBouncer is being precise: *transaction*-level advisory locks are not listed as broken, because they aren't.

Footnotes, verbatim:
> ¹ "Startup parameters are: client_encoding, DateStyle, IntervalStyle, Timezone, standard_conforming_strings, and application_name. PgBouncer detects their changes and so it can guarantee they remain consistent for the client. If you need PgBouncer to support more than these, take a look at track_extra_parameters and ignore_startup_parameters."
> ² "You need to change max_prepared_statements to a non-zero value to enable this support."

**The PgBouncer FAQ (<https://www.pgbouncer.org/faq.html>) does not mention advisory locks at all.** The features table is the only place PgBouncer documents this.

#### 4.2 `server_reset_query` and `server_reset_query_always`, verbatim

From <https://www.pgbouncer.org/config.html>:

> **`server_reset_query`**
> "Query sent to server on connection release, before making it available to other clients. At that moment no transaction is in progress, so the value should not include `ABORT` or `ROLLBACK`.
> The query is supposed to clean any changes made to the database session so that the next client gets the connection in a well-defined state. The default is `DISCARD ALL`, which cleans everything, but that leaves the next client no pre-cached state. It can be made lighter, e.g. `DEALLOCATE ALL` to just drop prepared statements, if the application does not break when some state is kept around.
> **When transaction pooling is used, the `server_reset_query` is not used, because in that mode, clients must not use any session-based features, since each transaction ends up in a different connection and thus gets a different session state.**
> Default: `DISCARD ALL`"

> **`server_reset_query_always`**
> "Whether `server_reset_query` should be run in all pooling modes. When this setting is off (default), the `server_reset_query` will be run only in pools that are in sessions-pooling mode. Connections in transaction-pooling mode should not have any need for a reset query.
> **This setting is for working around broken setups that run applications that use session features over a transaction-pooled PgBouncer. It changes non-deterministic breakage to deterministic breakage: Clients always lose their state after each transaction.**
> Default: 0"

That last sentence is the single best quote in this whole research file. It is PgBouncer's maintainers saying, in their own words, that there is no configuration that makes session advisory locks *work* in transaction mode — only one that makes them *fail predictably*.

Note the wording is "only in pools that are in **sessions-pooling** mode" — so `server_reset_query` is not run in **statement** mode either.

Confirmed live against a running PgBouncer 1.25.2 (`SHOW CONFIG` on the `pgbouncer` admin DB):

```
 pool_mode                 | transaction  | session     | yes
 server_reset_query        | DISCARD ALL  | DISCARD ALL | yes
 server_reset_query_always | 0            | 0           | yes
```

Note `server_reset_query`'s *value* is still `DISCARD ALL` in transaction mode — it is **not** blanked out. It simply is not executed. Anyone auditing config by reading `SHOW CONFIG` will see `DISCARD ALL` and wrongly conclude they are protected.

#### 4.3 Does `DISCARD ALL` release advisory locks? Yes — definitively

<https://www.postgresql.org/docs/current/sql-discard.html>, `DISCARD ALL`:

> "Releases all temporary resources associated with the current session and resets the session to its initial state. Currently, this has the same effect as executing the following sequence of statements:"
>
> ```sql
> CLOSE ALL;
> SET SESSION AUTHORIZATION DEFAULT;
> RESET ALL;
> DEALLOCATE ALL;
> UNLISTEN *;
> SELECT pg_advisory_unlock_all();
> DISCARD PLANS;
> DISCARD TEMP;
> DISCARD SEQUENCES;
> ```

`SELECT pg_advisory_unlock_all();` is right there in the documented equivalence. Note also that `pg_advisory_unlock_all()` releases *all* stacked holds at once, regardless of depth.

Confirmed empirically:

```sql
SELECT pg_advisory_lock(500); SELECT pg_advisory_lock(500); SELECT pg_advisory_lock_shared(501);
SELECT count(*) FROM pg_locks WHERE locktype='advisory';  -- before_discard: 2
DISCARD ALL;
SELECT count(*) FROM pg_locks WHERE locktype='advisory';  -- after_discard:  0
```

Also note `DISCARD PLANS` / `DISCARD SEQUENCES` / `DISCARD TEMP` alone do **not** touch advisory locks — only `ALL` does. So a "lightened" `server_reset_query = DEALLOCATE ALL` (a commonly recommended tuning, and one PgBouncer's own docs suggest) **removes the advisory-lock cleanup even in session mode.**

#### 4.4 What actually breaks, precisely — measured

Setup: `postgres:17`, PgBouncer 1.25.2, `pool_mode = transaction`, `default_pool_size = 1`, defaults otherwise.

**Step 1 — Client A takes a session advisory lock through the pooler and disconnects.**

```
$ psql -h pgbouncer -p 6432 -c "SELECT pg_advisory_lock(7777); SELECT pg_backend_pid();"
 pg_advisory_lock |
 server_pid       | 93
```

**Step 2 — Client A is gone. Inspect the server directly (bypassing PgBouncer):**

```
 locktype | classid | objid | objsubid | pid | granted
----------+---------+-------+----------+-----+---------
 advisory |       0 |  7777 |        1 |  93 | t
```

**The lock is still held.** Client A's TCP connection closed, but PgBouncer's server connection to PostgreSQL did not — from PostgreSQL's point of view the session never ended, so `pg_advisory_unlock_all()` at session end never fired. And PgBouncer did not run `DISCARD ALL` because transaction mode does not run `server_reset_query`. Nothing errored. Nothing was logged. The lock is simply orphaned, owned by a client that no longer exists.

**Step 3 — an unrelated Client B connects and lands on the same pooled backend:**

```
$ psql -h pgbouncer -p 6432 -c "SELECT pg_backend_pid(), pg_try_advisory_lock(7777);"
 server_pid | did_b_get_the_lock
------------+--------------------
         93 | t
```

**`pg_try_advisory_lock` returned `true` for a lock B does not logically own.** This is the sharp edge and it is much worse than "the lock leaks". PostgreSQL is behaving exactly as documented — *"If a session already holds a given advisory lock, additional requests by it will always succeed"* — but "session" now means "PgBouncer's server connection", not "your application's unit of work". **Mutual exclusion is silently violated**: your critical section runs twice concurrently and every `try_lock` says yes.

**Step 4 — meanwhile a client that lands on a *different* backend is starved.** Against a leaked lock, a direct (unpooled) client:

```
$ psql -c "SET lock_timeout='3s'; SELECT pg_advisory_lock(4242);"
ERROR:  canceling statement due to lock timeout
```

It would have waited forever. So the same misconfiguration produces **both** failure modes depending on which backend you get — a lost mutex if you share the poisoned backend, an indefinite hang if you don't. That non-determinism is exactly what PgBouncer's docs call "non-deterministic breakage".

**The lock also survives across the client's own transactions but on a lottery of backends:**

```sql
BEGIN; SELECT pg_advisory_lock(4242); COMMIT;
BEGIN; SELECT count(*) FROM pg_locks WHERE objid=4242; COMMIT;  -- 1, but only because pool_size=1
```

With `default_pool_size > 1` the second transaction may be routed to a different backend and see nothing.

**Step 5 — `server_reset_query_always = 1` in transaction mode:**

```sql
-- with server_reset_query_always = 1
BEGIN; SELECT pg_advisory_lock(4242); COMMIT;
BEGIN; SELECT count(*) FROM pg_locks WHERE objid=4242; COMMIT;
-- still_held_in_next_txn: 0
```

The leak is gone, but so is the lock — released at the end of the very transaction that took it. Session-scoped advisory locks are now **functionally identical to transaction-scoped ones, just slower and more surprising**. This is precisely PgBouncer's "changes non-deterministic breakage to deterministic breakage". It is a good safety net; it is not a way to make session locks work.

**Step 6 — control: `pg_advisory_xact_lock` through transaction pooling.**

```sql
BEGIN; SELECT pg_advisory_xact_lock(9999); SELECT pg_backend_pid(); COMMIT;
-- after the client disconnects:
SELECT count(*) FROM pg_locks WHERE locktype='advisory';  -- leaked: 0
```

Clean. Every time.

**Step 7 — control: session pooling mode.**

```sql
-- pool_mode = session
SELECT pg_advisory_lock(7777);
SELECT count(*) FROM pg_locks WHERE locktype='advisory';  -- held_within_session: 1
-- client disconnects; PgBouncer runs server_reset_query = DISCARD ALL
SELECT count(*) FROM pg_locks WHERE locktype='advisory';  -- leaked: 0
```

Works and cleans up — matching the "Yes" in the feature matrix. Note the cleanup depends entirely on `server_reset_query` still containing `DISCARD ALL`.

#### 4.5 The defensible conclusion

- **Session pooling + session advisory locks: SAFE**, on two conditions — (a) `server_reset_query` still includes `DISCARD ALL` (do not "lighten" it to `DEALLOCATE ALL`), and (b) you accept that a pooled server connection is pinned for the client's whole lifetime, which is most of the reason people reach for a pooler in the first place.
- **Transaction pooling + session advisory locks: UNSAFE, and unsafe silently.** Documented as "Never". The lock is acquired successfully and then abandoned on a pooled backend. Two distinct failure modes result: spurious `true` from `pg_try_advisory_lock` (mutual exclusion lost) for clients that reuse the poisoned backend, and indefinite waits for clients that do not. `server_reset_query_always = 1` converts this to deterministic breakage — the lock is dropped at the next transaction boundary — which is a guardrail, not a fix.
- **Statement pooling + session advisory locks: UNSAFE**, at least as badly. It is transaction pooling with multi-statement transactions disallowed, and `server_reset_query` runs only in session-pooling pools.
- **Transaction-scoped advisory locks (`pg_advisory_xact_lock`, `pg_try_advisory_xact_lock`, and the `_shared` variants): SAFE in all three pooling modes**, because their lifetime is bounded by the transaction, and the transaction is PgBouncer's unit of checkout. The one requirement is that the lock and the work it protects are in the **same explicit transaction** — an autocommit `SELECT pg_advisory_xact_lock(k)` on its own line takes and immediately drops the lock, protecting nothing.
- **Statement mode caveat for `xact` locks:** statement mode disallows multi-statement transactions, so you cannot hold an `xact` advisory lock across several statements there either. Advisory locks are effectively unusable in statement mode for anything but a single-statement critical section.
- The failure is **not** detectable by the application. There is no error, no warning, no log line. The only signal is `pg_locks` rows whose backend is `idle` in `pg_stat_activity`.

---

### 5. Practical guidance

#### 5.1 Default to `pg_advisory_xact_lock`

Make transaction-scoped the default and treat session-scoped as the exception requiring justification:

- It is pooler-safe in every mode (§4).
- It cannot leak: no `finally` block, no unlock counting, no "did the process crash between lock and unlock" question. Crash, `ROLLBACK`, statement error, client disconnect — the lock goes.
- The docs themselves nudge this way: *"This behavior is often more convenient than the session-level behavior for short-term usage of an advisory lock."* (§13.3.5)
- It composes with your existing transaction boundary, which is usually where the critical section already lives.

Reach for **session-scoped** only when the critical section genuinely spans multiple transactions and you control the connection end-to-end (a dedicated worker, an unpooled connection, or a session-mode pool) — classic cases being a long-running singleton daemon, or a schema migration runner that must hold a lock across many commits.

#### 5.2 Advantages over `SELECT ... FOR UPDATE`

- **No row needs to exist.** You can lock "the concept of order 1001" before the order row is written — the canonical fix for insert-or-update races that `FOR UPDATE` cannot express (`FOR UPDATE` locks nothing if the `SELECT` returns zero rows).
- **Arbitrary keys.** You can lock across tables, across a whole tenant, or on something with no database representation at all ("the nightly reconciliation job").
- **No table bloat and no dead tuples.** The docs make exactly this argument for advisory locks over a flag column: *"While a flag stored in a table could be used for the same purpose, advisory locks are faster, avoid table bloat, and are automatically cleaned up by the server at the end of the session."* (§13.3.5). A `FOR UPDATE` on a real row also pins the transaction and holds a `transactionid` lock; an advisory lock does not touch heap tuples.
- **No `xmax`/`UPDATE` write amplification**, and no interaction with `REPEATABLE READ` serialisation failures on the locked row.
- **Cheap and uniform** — one shared-memory lock entry regardless of how many rows the critical section touches.

#### 5.3 Disadvantages, honestly

- **Advisory, not enforced.** The docs open with it: *"the system does not enforce their use — it is up to the application to use them correctly."* One code path that forgets the lock defeats it entirely. `FOR UPDATE` protects the row no matter who touches it.
- **Invisible to ORMs and to code review.** They do not appear in the schema, in migrations, or in a query plan. A developer reading the code sees a function call; nothing tells them which other subsystems share that key. The two-`int4` form (§2.3) is the cheapest mitigation available.
- **A flat, unnamespaced key space** shared with every library in your dependency tree.
- **They count against `max_locks_per_transaction * max_connections`** and are never fast-path — a leaking session-lock pattern can genuinely exhaust the lock table and take down *all* locking on the server (§1.5).
- **`pg_advisory_unlock` fails with a `WARNING`, not an `ERROR`** — silent when the unlock is wrong.
- **They are per-database, not per-cluster** — surprising if you shard by database.
- **They do not survive failover** — advisory locks live in shared memory only, are not replicated, and are not visible on a standby.
- **No timeout parameter on the function itself.** See below — but the workaround is better than usually claimed.

#### 5.4 Timeouts — `lock_timeout` and `statement_timeout` DO apply (verified)

There is no `pg_advisory_lock(key, timeout)`. The docs for `lock_timeout` say — <https://www.postgresql.org/docs/current/runtime-config-client.html>:

> "Abort any statement that waits longer than the specified amount of time while attempting to acquire a lock on a table, index, row, **or other database object**. The time limit applies separately to each lock acquisition attempt. The limit applies both to explicit locking requests (such as `LOCK TABLE`, or `SELECT FOR UPDATE` without `NOWAIT`) and to implicitly-acquired locks."

The docs **never say the word "advisory"** here — which is why this is widely, and wrongly, assumed not to work. It does. Advisory locks are "other database object", and the mechanism confirms it: `LockAcquire` → `WaitOnLock` → `ProcSleep`, and `ProcSleep` in `src/backend/storage/lmgr/proc.c` arms the timeout for **every** heavyweight lock wait regardless of lock method:

```c
if (LockTimeout > 0)
{
    EnableTimeoutParams timeouts[2];
    timeouts[0].id = DEADLOCK_TIMEOUT;  timeouts[0].delay_ms = DeadlockTimeout;
    timeouts[1].id = LOCK_TIMEOUT;      timeouts[1].delay_ms = LockTimeout;
    enable_timeouts(timeouts, 2);
}
```

Source: <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/storage/lmgr/proc.c>

Measured on 17.11, against a held lock:

```
SET lock_timeout = '1500ms';
SELECT pg_advisory_lock(999);
ERROR:  canceling statement due to lock timeout
Time: 1500.683 ms

SET statement_timeout = '1500ms';
SELECT pg_advisory_xact_lock(999);
ERROR:  canceling statement due to statement timeout
Time: 1500.559 ms

SET lock_timeout = '1000ms'; BEGIN; SELECT pg_advisory_xact_lock(999);
ERROR:  canceling statement due to lock timeout
```

Both work, on both scopes, and both fire at the configured deadline. Prefer **`lock_timeout`**: it is scoped to the lock wait specifically, produces a distinguishable error (`SQLSTATE 55P03 lock_not_available` vs `57014 query_canceled`), and does not put a ceiling on the *work* inside the critical section.

`statement_timeout` also works but is blunter — it caps the whole statement, so if you write `SELECT pg_advisory_xact_lock(k)` as its own statement it behaves like `lock_timeout`, but it will also cancel long legitimate work.

The third option, and often the best, is `pg_try_advisory_xact_lock()` with application-level retry/backoff: no waiting at all, an immediate boolean, and no chance of piling up waiters in the lock table.

**Important caveat when using `lock_timeout` with the poll-free wait:** the docs note *"The time limit applies separately to each lock acquisition attempt."* A statement that takes several advisory locks can therefore exceed `lock_timeout` in total.

---

### 6. Does `SELECT ... FOR UPDATE` survive PgBouncer transaction mode?

**Yes — confirmed.** Row-level locks are strictly transaction-scoped: PostgreSQL's row-locking section states verbatim: *"Row-level locks are released at transaction end or during savepoint rollback, just like table-level locks."* (<https://www.postgresql.org/docs/current/explicit-locking.html>, §13.3.2). Since PgBouncer's transaction-mode checkout unit is exactly one transaction, the lock cannot outlive the checkout. `FOR UPDATE` does not appear anywhere in PgBouncer's list of broken features.

Measured through PgBouncer 1.25.2 in transaction mode:

```sql
BEGIN;
  SELECT * FROM acct WHERE id=1 FOR UPDATE;         -- id=1, bal=100
  SELECT count(*) FROM pg_locks
    WHERE locktype IN ('transactionid','tuple');    -- row_locks: 1
  UPDATE acct SET bal=bal-10 WHERE id=1;
COMMIT;
SELECT bal FROM acct WHERE id=1;                    -- 90
-- server side, after the client disconnected:
SELECT count(*) FROM pg_locks
  WHERE locktype IN ('transactionid','tuple');      -- leftover: 0
```

Correct result, nothing left behind.

The **one real caveat** is not the lock, it is the transaction. In transaction pooling, the whole `BEGIN … FOR UPDATE … UPDATE … COMMIT` must be issued as **one server-side transaction**. An ORM or framework that runs the `SELECT ... FOR UPDATE` outside an explicit transaction (autocommit) takes and instantly drops the lock, protecting nothing — and this failure looks identical whether you are pooled or not. The other caveat is that a long `FOR UPDATE` transaction **pins a pooled server connection for its whole duration**, so it consumes pool capacity in a way an advisory `try_lock` does not. Neither is a correctness problem with the lock itself.

The same reasoning covers `FOR NO KEY UPDATE`, `FOR SHARE`, `FOR KEY SHARE`, `LOCK TABLE`, and `pg_advisory_xact_lock`: **anything whose lifetime is the transaction is safe under transaction pooling; anything whose lifetime is the session is not.** That is the single rule the whole talk can hang on.

---

## Demo notes

### Demo 0 — setup (self-contained, no cloud)

```bash
docker run -d --name pgadv -e POSTGRES_HOST_AUTH_METHOD=trust -p 55432:5432 postgres:17
psql -h localhost -p 55432 -U postgres
```

A live-lock viewer to keep on a second screen:

```sql
CREATE OR REPLACE VIEW adv AS
SELECT l.pid,
       CASE l.objsubid WHEN 1 THEN 'bigint' WHEN 2 THEN 'int4,int4' END AS keyform,
       CASE l.objsubid
            WHEN 1 THEN ((l.classid::bigint << 32) | l.objid::bigint)::text
            WHEN 2 THEN l.classid || ',' || l.objid
       END AS key,
       l.mode, l.granted, a.state, a.application_name
FROM pg_locks l JOIN pg_stat_activity a USING (pid)
WHERE l.locktype = 'advisory';

SELECT * FROM adv;
```

`state = 'idle'` on a granted advisory row is the tell-tale of a session-scoped lock — and, in production behind a pooler, of a leak.

### Demo 1 — session vs transaction scope, and the rollback difference

Session **A**:

```sql
BEGIN;
  SELECT pg_advisory_lock(100);       -- session-scoped
  SELECT pg_advisory_xact_lock(200);  -- transaction-scoped
  SELECT * FROM adv;                  -- both visible
ROLLBACK;

SELECT * FROM adv;
-- key 100 is STILL HELD. key 200 is gone.
-- "The rollback undid your data. It did not undo your lock."
```

Then show that the escape hatch also ignores the transaction:

```sql
BEGIN; SELECT pg_advisory_unlock(100); ROLLBACK;
SELECT * FROM adv;   -- empty. The unlock stuck even though the txn rolled back.
```

### Demo 2 — stacking and unlock counting (the off-by-one)

```sql
SELECT pg_advisory_lock(42);
SELECT pg_advisory_lock(42);
SELECT pg_advisory_lock(42);

SELECT count(*) FROM adv WHERE key = '42';   -- 1 row. Looks like ONE lock.

SELECT pg_advisory_unlock(42);               -- t
SELECT count(*) FROM adv WHERE key = '42';   -- STILL 1. Still held.

SELECT pg_advisory_unlock(42);               -- t
SELECT pg_advisory_unlock(42);               -- t
SELECT count(*) FROM adv WHERE key = '42';   -- 0

SELECT pg_advisory_unlock(42);
-- WARNING:  you don't own a lock of type ExclusiveLock
-- f
```

Punchline: `pg_locks` shows one row no matter how deep the stack, so **you cannot see the leak coming**, and the over-unlock is a `WARNING` your driver will almost certainly swallow.

### Demo 3 — the two key spaces do not collide

```sql
SELECT pg_advisory_lock(0::bigint);   -- takes it
SELECT pg_try_advisory_lock(0, 0);    -- t  <-- ALSO takes it

SELECT classid, objid, objsubid, mode FROM pg_locks WHERE locktype='advisory';
--  classid | objid | objsubid |     mode
--        0 |     0 |        1 | ExclusiveLock   <- bigint form
--        0 |     0 |        2 | ExclusiveLock   <- (int4,int4) form
```

Then the collision that *does* bite, in the same key space:

```sql
SELECT pg_typeof(hashtext('anything'));   -- integer  <- only 2^32 of the 2^64 space
SELECT hashtext('order:1001'), hashtext('invoice:77');
-- two unrelated business concepts, one flat 32-bit namespace, ~77k keys to a coin-flip collision
SELECT pg_typeof(hashtextextended('anything', 0));  -- bigint  <- use this instead
```

### Demo 4 — timeouts really do work (kill the myth)

Session **A**: `SELECT pg_advisory_lock(999);` and leave it.

Session **B**:

```sql
\timing on
SET lock_timeout = '2s';
SELECT pg_advisory_lock(999);
-- ERROR:  canceling statement due to lock timeout
-- Time: 2000.xxx ms
```

### Demo 5 — deadlock detection covers advisory locks

Session **A**: `BEGIN; SELECT pg_advisory_xact_lock(1001);`
Session **B**: `BEGIN; SELECT pg_advisory_xact_lock(1002);`
Session **A**: `SELECT pg_advisory_xact_lock(1002);`  → blocks
Session **B**: `SELECT pg_advisory_xact_lock(1001);`  → after `deadlock_timeout` (default 1s):

```
ERROR:  deadlock detected
DETAIL:  Process 90 waits for ExclusiveLock on advisory lock [5,0,1001,1]; blocked by process 89.
         Process 89 waits for ExclusiveLock on advisory lock [5,0,1002,1]; blocked by process 90.
```

Point at `[5,0,1001,1]` = `[database, classid, objid, objsubid]` and note the trailing `1` is the key-form discriminator from §2.1.

### Demo 6 — THE MONEY DEMO: the pooled-connection leak

```bash
mkdir -p /tmp/pgb
cat > /tmp/pgb/pgbouncer.ini <<'EOF'
[databases]
postgres = host=pgadv port=5432 dbname=postgres

[pgbouncer]
listen_addr = 0.0.0.0
listen_port = 6432
auth_type = trust
auth_file = /etc/pgbouncer/userlist.txt
pool_mode = transaction
max_client_conn = 100
default_pool_size = 1
ignore_startup_parameters = extra_float_digits
admin_users = postgres
EOF
echo '"postgres" ""' > /tmp/pgb/userlist.txt

docker network create advnet; docker network connect advnet pgadv
docker run -d --name pgbtest --network advnet -p 56432:6432 \
  -v /tmp/pgb/pgbouncer.ini:/etc/pgbouncer/pgbouncer.ini:ro \
  -v /tmp/pgb/userlist.txt:/etc/pgbouncer/userlist.txt:ro \
  edoburu/pgbouncer:latest
```

Show the config trap first:

```bash
psql -h localhost -p 56432 -U postgres -d pgbouncer -c "SHOW CONFIG" | grep reset
#  server_reset_query        | DISCARD ALL | ...
#  server_reset_query_always | 0           | ...
```

> "It says `DISCARD ALL`. It is not running it."

**Act 1 — Client A takes a session lock through the pooler, then leaves:**

```bash
psql -h localhost -p 56432 -U postgres -c \
  "SELECT pg_advisory_lock(7777); SELECT pg_backend_pid();"
#  server_pid | 93
```

**Act 2 — Client A is gone. Look at the database directly (port 55432, bypassing PgBouncer):**

```bash
psql -h localhost -p 55432 -U postgres -c "SELECT * FROM adv;"
#  pid | keyform |  key | mode          | granted | state
#   93 | bigint  | 7777 | ExclusiveLock | t       | idle
```

> "The client is gone. The lock is not. And look at `state` — `idle`. Nobody is even in a transaction."

**Act 3 — an unrelated Client B connects and gets the poisoned backend:**

```bash
psql -h localhost -p 56432 -U postgres -c \
  "SELECT pg_backend_pid(), pg_try_advisory_lock(7777);"
#  server_pid | 93
#  pg_try_advisory_lock | t     <-- !!!
```

> "`try_lock` said **yes**. B does not own that lock. Your mutex just let two workers into the critical section, and neither of them will ever know."

**Act 4 — the other failure mode.** A client that lands on a *different* backend hangs instead:

```bash
psql -h localhost -p 55432 -U postgres -c \
  "SET lock_timeout='3s'; SELECT pg_advisory_lock(7777);"
# ERROR:  canceling statement due to lock timeout
```

> "Same bug. Same config. Two completely different symptoms depending on which backend you happen to get. That's what PgBouncer's docs mean by *non-deterministic breakage*."

**Act 5 — the fix.** Change one word:

```bash
psql -h localhost -p 56432 -U postgres -c \
  "BEGIN; SELECT pg_advisory_xact_lock(7777); SELECT pg_backend_pid(); COMMIT;"
psql -h localhost -p 55432 -U postgres -c "SELECT count(*) FROM adv;"
#  0
```

**Act 6 (optional) — the guardrail, and why it is not a fix.** Add `server_reset_query_always = 1`, restart PgBouncer, then:

```bash
psql -h localhost -p 56432 -U postgres <<'EOF'
BEGIN; SELECT pg_advisory_lock(4242); COMMIT;
BEGIN; SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid=4242; COMMIT;
EOF
#  count | 0     <-- the client lost its own lock at the transaction boundary
```

> "The leak is gone. So is the lock. PgBouncer's own docs call this 'changes non-deterministic breakage to deterministic breakage'."

### Demo 7 — control: `FOR UPDATE` is fine

```bash
psql -h localhost -p 55432 -U postgres -c \
  "CREATE TABLE acct(id int primary key, bal int); INSERT INTO acct VALUES (1,100);"

psql -h localhost -p 56432 -U postgres -c \
  "BEGIN; SELECT * FROM acct WHERE id=1 FOR UPDATE; UPDATE acct SET bal=bal-10 WHERE id=1; COMMIT;"

psql -h localhost -p 55432 -U postgres -c \
  "SELECT count(*) FROM pg_locks WHERE locktype IN ('transactionid','tuple');"
#  0
```

### Cleanup

```bash
docker rm -f pgbtest pgadv; docker network rm advnet
```

---

## Talk-ready points

- "Session-level advisory locks are the only PostgreSQL lock that survives `ROLLBACK`. If your rollback is the thing you're relying on to clean up, it isn't going to."
- "Session advisory locks stack, but `pg_locks` shows you one row no matter how many times you took it. If you lock three times you must unlock three times — and the fourth unlock returns `false` with a `WARNING`, not an error, so your driver will almost certainly throw it away."
- "The one-`bigint` form and the two-`int4` form are genuinely different lock spaces — the source stores a `1` or a `2` in the fourth locktag field. `pg_advisory_lock(0::bigint)` and `pg_advisory_lock(0,0)` both succeed at the same time. The collisions that actually hurt you are *within* one space: everybody who hashes a string into a key is sharing one flat, unnamespaced, per-database namespace with every library in your dependency tree."
- "If you use `hashtext()` to build a key, you are using 32 bits of a 64-bit space. Coin-flip odds of a collision at about 77,000 keys. Use `hashtextextended()` — or better, use the two-int form and put a subsystem ID in the first slot so `pg_locks` tells you *who* holds it."
- "PgBouncer's feature matrix has exactly one row about locks, and it reads: *Session-level advisory locks — session pooling: Yes; transaction pooling: **Never***. Note the wording: 'session-level'. Transaction-level advisory locks aren't on that list, because they aren't broken."
- "Here's what 'Never' actually means, and it's worse than you think. The lock doesn't fail. It succeeds. Then PgBouncer hands that server connection to somebody else, still holding your lock — and because PostgreSQL sees the same session, `pg_try_advisory_lock` returns **true** to a client that doesn't own it. Your mutex silently lets two workers in, and nothing anywhere logs a thing."
- "`DISCARD ALL` does release advisory locks — the Postgres docs literally list `SELECT pg_advisory_unlock_all()` in its definition. And PgBouncer's `server_reset_query` defaults to `DISCARD ALL`. The trap is that `server_reset_query` is only executed in **session-pooling** pools. In transaction mode `SHOW CONFIG` still proudly says `DISCARD ALL` — it just never runs it."
- "There is a setting called `server_reset_query_always` that runs the reset in transaction mode too. PgBouncer's own docs describe it as: *'It changes non-deterministic breakage to deterministic breakage: Clients always lose their state after each transaction.'* That's the maintainers telling you there is no config that makes session locks work — only one that makes them fail predictably."
- "Everyone says advisory locks have no timeout. They have two. `lock_timeout` and `statement_timeout` both work on advisory lock waits — I measured it — because advisory waits go through the same `ProcSleep` path as every other heavyweight lock. Prefer `lock_timeout`: distinct SQLSTATE, and it doesn't cap the work inside your critical section."
- "One rule covers the whole topic: **anything whose lifetime is the transaction is safe under transaction pooling; anything whose lifetime is the session is not.** `FOR UPDATE`, `LOCK TABLE`, `pg_advisory_xact_lock` — all fine. `pg_advisory_lock`, `LISTEN`, `SET`, `WITH HOLD` cursors — all broken. Make `pg_advisory_xact_lock` your default and session scope the thing you have to justify."
- "Advisory locks aren't free. They're never fast-path, they sit in the same shared-memory pool as every other lock sized by `max_locks_per_transaction × max_connections`, and a leaking session-lock pattern behind a pooler can exhaust that table and stop *all* locking on the server — not just yours."

---

## Sources

**Primary — PostgreSQL official documentation**

- <https://www.postgresql.org/docs/current/explicit-locking.html> — Explicit Locking chapter. §13.3.5 "Advisory Locks" is the authoritative statement of session vs transaction semantics, rollback behaviour, stacking/unlock counting, the shared-memory pool limit, and the `LIMIT` evaluation-order trap. §13.3.2 "Row-Level Locks" for `FOR UPDATE` lifetime.
- <https://www.postgresql.org/docs/current/functions-admin.html> — System Administration Functions. §9.28.10 "Advisory Lock Functions", Table 9.109: all function signatures for both key forms, plus the verbatim "note that these two key spaces do not overlap" and the "Multiple session-level lock requests stack" sentence.
- <https://www.postgresql.org/docs/current/sql-discard.html> — `DISCARD` reference page. Defines `DISCARD ALL` as an explicit statement sequence that includes `SELECT pg_advisory_unlock_all();`.
- <https://www.postgresql.org/docs/current/view-pg-locks.html> — `pg_locks` column definitions, the `locktype` enumeration including `advisory`, and `classid`/`objid`/`objsubid` semantics.
- <https://www.postgresql.org/docs/current/runtime-config-client.html> — `lock_timeout` and `statement_timeout` definitions.
- <https://www.postgresql.org/docs/current/runtime-config-locks.html> — `max_locks_per_transaction`.

**Primary — PostgreSQL source code**

- <https://github.com/postgres/postgres/blob/master/src/backend/utils/adt/lockfuncs.c> — the advisory-lock locktag layout comment (`field1..field4`, incl. the `1` vs `2` key-form discriminator), `SET_LOCKTAG_INT64`/`SET_LOCKTAG_INT32`, every `pg_advisory_*` implementation showing the `sessionLock` argument, and the `case LOCKTAG_ADVISORY` block in `pg_lock_status()` that populates `pg_locks`.
- <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/include/storage/lock.h> — `LockTagType` enum (`LOCKTAG_ADVISORY`), the `SET_LOCKTAG_ADVISORY` macro (`USER_LOCKMETHOD`), and `LOCALLOCKOWNER`/`LOCALLOCK` showing the per-owner `nLocks` counter that implements stacking.
- <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/storage/lmgr/lock.c> — `LockAcquire` header comment documenting `sessionLock` and `LOCKACQUIRE_ALREADY_HELD`; `default_lockmethod` / `user_lockmethod` tables; `WaitOnLock` → `ProcSleep` wait path.
- <https://github.com/postgres/postgres/blob/REL_17_STABLE/src/backend/storage/lmgr/proc.c> — `ProcSleep`, showing `LOCK_TIMEOUT` armed for every heavyweight lock wait (the mechanism behind `lock_timeout` applying to advisory locks).

**Primary — PgBouncer official documentation**

- <https://www.pgbouncer.org/features.html> — pooling-mode descriptions and the "SQL feature map for pooling modes" table containing the "Session-level advisory locks / Yes / Never" row. Verified against the raw HTML (the published table has only Session and Transaction columns).
- <https://www.pgbouncer.org/config.html> — `pool_mode`, `server_reset_query` (including "When transaction pooling is used, the `server_reset_query` is not used"), `server_reset_query_always` (including "changes non-deterministic breakage to deterministic breakage"), `server_check_query`.
- <https://www.pgbouncer.org/faq.html> — checked; contains **no** mention of advisory locks. Relevant only as evidence of what PgBouncer does *not* document.

**Primary — reproduction environment (measurements in this document)**

- PostgreSQL 17.11 (Docker `postgres:17`, Debian build) — all `pg_locks`, stacking, rollback, savepoint, `DISCARD ALL`, timeout, and deadlock measurements.
- PgBouncer 1.25.2 (Docker `edoburu/pgbouncer`) — all pooling-mode measurements, `SHOW CONFIG` output, and the leak demonstration.

**Secondary — pointers only, not relied on for any claim above**

- <https://github.com/pgbouncer/pgbouncer/issues/102> — "Need reset_query in transaction mode with pg_advisory_lock" (PgBouncer's own tracker). *Secondary; not used as evidence.*
- <https://github.com/pgbouncer/pgbouncer/issues/110> — "New server_reset_query_always default introduces non-deterministic behavior." *Secondary.*
- <https://github.com/bensheldon/good_job/issues/52> — "Document issues with PgBouncer and session-level Advisory Locks." *Secondary; useful as evidence that real libraries hit this.*
- <https://github.com/ClosureTree/with_advisory_lock/issues/43> — advisory locks with PgBouncer, Rails ecosystem. *Secondary.*

---

## Unverified / open

1. **Savepoint behaviour is documented for row and table locks, but NOT for advisory locks.** §13.3.2 states verbatim: *"Row-level locks are released at transaction end or during savepoint rollback, just like table-level locks."* (<https://www.postgresql.org/docs/current/explicit-locking.html>) — so the `LOCK TABLE` comparison in §1.4 above is documented behaviour, not just a measurement. What is **not** documented is the advisory case: I measured that a transaction-level advisory lock taken inside a savepoint **is** released by `ROLLBACK TO SAVEPOINT` on PostgreSQL 17.11, but found no doc line covering it in §13.3.5. Treat that specific result as *measured*, not *contracted*.

2. **Statement-pooling mode is inferred, not directly documented.** PgBouncer's feature matrix has no Statement column. My conclusion that session advisory locks are equally broken there rests on (a) the prose "This is transaction pooling with a twist" and (b) `server_reset_query_always`'s wording that the reset runs "only in pools that are in sessions-pooling mode". I did **not** run the leak experiment against `pool_mode = statement`. The inference is strong but it is an inference.

3. **PgBouncer version sensitivity.** All PgBouncer measurements are against 1.25.2. The `server_reset_query` / `server_reset_query_always` semantics have changed historically (issue #110 in the pointer list concerns a default change). If your production PgBouncer is materially older, re-check `SHOW CONFIG` rather than trusting these numbers.

4. **`prepared transactions` / two-phase commit interaction not tested.** `lockfuncs.c` and the 2PC lock-record machinery suggest advisory locks are not carried into a prepared transaction, but I did not test `PREPARE TRANSACTION` with an advisory lock held. Do not assert anything about this on stage.

5. **Other poolers not examined.** Odyssey, pgcat, Supavisor, RDS Proxy and AWS/Azure managed poolers each have their own reset semantics. The `FOR UPDATE`-vs-session-lock rule should generalise (it follows from transaction-scope vs session-scope, not from PgBouncer specifically), but the specific `server_reset_query` behaviour does not. Nothing here was verified against any pooler other than PgBouncer.

6. **`hashtext()` cross-version stability.** I confirmed `hashtext()` returns `integer` and `hashtextextended()` returns `bigint` on 17.11. I did **not** find an explicit documentation statement guaranteeing (or disclaiming) hash-value stability across major versions or platforms; the "not guaranteed stable" claim in §2.3 is the widely-held convention among Postgres hackers, and it is the safe assumption, but I could not cite a doc line for it. If you want to state it on stage, phrase it as "don't rely on it" rather than "the docs say".

7. **Lock-table exhaustion not reproduced.** The `max_locks_per_transaction` limit and the "tens to hundreds of thousands" figure are quoted from §13.3.5. I confirmed advisory locks are never fast-path (`fastpath = f`) but did not actually exhaust a lock table to observe the failure mode.

8. **The `objsubid`/key-form mapping was verified on PostgreSQL 17.** The locktag comment in `lockfuncs.c` is identical on `master`, and this layout has been stable for many major versions, but I only measured on 17.11.
