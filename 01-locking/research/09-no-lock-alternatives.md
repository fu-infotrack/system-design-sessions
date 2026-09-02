# Q9 — How Not to Need a Lock: Nine Alternatives, Constraints First

## Summary

Every lock is a way of saying "I will personally prevent the bad interleaving." The nine families below all say
something different: *make the bad interleaving impossible, or make it detectable and cheap to redo.* The first and
most under-used is to let the **database enforce the invariant declaratively** — a `UNIQUE` index or an `EXCLUDE`
constraint is a lock you never have to acquire, never have to release, cannot leak, and which binds even the writers
who don't know the rule exists. From there the ladder runs through optimistic concurrency (detect the conflict at
write time via a version token), idempotency keys (make the *retry* safe rather than the *concurrency*), structural
single-writer designs (Kafka partitions, Orleans grains — contention removed rather than defended against),
in-process CAS and immutable data, database serializable isolation (let the engine detect the anomaly and make you
retry), append-only/event-sourced/CRDT models (no write-write conflict to have), the outbox (one transaction instead
of two systems), and leases/leader election (the one case where a real distributed lock is genuinely the answer —
and where you must be honest that it buys efficiency, not correctness). The through-line: **a lock is a coordination
mechanism; almost every alternative here replaces coordination with either a structural guarantee or a
detect-and-retry loop.** The cost you trade into is almost always the same one — you must write the retry path, and
you must mean it.

---

## Quick reference

| Approach | Use when | Avoid when | Stack fit |
| --- | --- | --- | --- |
| **1. DB constraints** (`UNIQUE`, partial unique, `EXCLUDE`, FK) | The invariant is expressible as a predicate over rows in one PG database, and "one writer wins, the other gets an error" is an acceptable outcome. This is the default answer for *"only one X"* and *"no two overlapping X"*. | The invariant needs external work before the decision (call an API, then decide), spans databases/services, or you must *serialise* rather than *reject*. Also avoid when conflicts are the common case — the error path becomes the hot path. | PG + Npgsql: perfect. `PostgresException.SqlState == "23505"` / `"23P01"` is the whole error handler. |
| **2. Optimistic concurrency (version token)** | Read-modify-write on a row you already have, **low contention**, and a lost update is unacceptable. Long "think time" between read and write (user editing a form). | Contention is high — retry storms convert a correctness mechanism into a throughput collapse. Also avoid if you can't safely re-run the work on retry. | EF Core `[Timestamp]`/`IsRowVersion()`; on PG that maps to the free `xmin` system column (`uint`). HTTP `ETag`/`If-Match` for the same idea across an API boundary. |
| **3. Idempotency keys** | The operation has an **external side effect** (charge a card, send an email, call a partner API) and the caller may retry. Strictly stronger than a lock for this case. | Purely internal state mutation with no external effect — constraints or OCC are cheaper. Also avoid if you can't durably store key→response before doing the work. | PG unique index on the key + `INSERT ... ON CONFLICT`; ASP.NET Core middleware reading an `Idempotency-Key` header. |
| **4. Single-writer / partitioning** | The work is naturally keyed (per-account, per-order, per-tenant) and you control the routing. Removes contention *structurally* — no lock exists because no two threads touch the same state. | The key is unknown until mid-operation, work spans keys, or you need cross-key transactions. Hot keys become hard throughput ceilings. | Kafka/Event Hubs partition key; Orleans grains (single-threaded per activation); Azure Service Bus sessions. |
| **5. Lock-free in-process** (`Interlocked`, immutable, `ConcurrentDictionary`) | Single process, tiny critical section, contended counter/flag/reference swap, or a shared read-mostly structure. | The critical section does I/O, allocates a lot, or must be atomic across more than one memory location. Hand-rolled CAS on pointers invites ABA. | `Interlocked.CompareExchange`, `System.Collections.Immutable`, `ConcurrentDictionary` (striped locks for writes, lock-free reads). |
| **6. Serializable isolation (PG SSI)** | The invariant spans **multiple rows or rows that don't exist yet** (phantoms, "sum of balances", "no more than N bookings"), and you'd rather write naive code than reason about lock order. | You cannot add a retry loop. High-conflict workloads (the abort rate is the cost). Long transactions. Also note SQL Server's `SERIALIZABLE` is lock-based, not SSI — same name, different behaviour. | PG `ISOLATION LEVEL SERIALIZABLE` + retry on SQLSTATE `40001`. Npgsql surfaces this as `PostgresException.SqlState`. |
| **7. Append-only / event sourcing / CRDTs** | Writes are naturally facts ("seat reserved", "payment received") rather than overwrites; you need an audit trail; or replicas must converge while partitioned (CRDT). | You mostly need current-state reads, the team hasn't done event sourcing before, or eventual consistency is unacceptable. CRDTs specifically: only when you genuinely need coordination-free convergence. | PG append-only tables; Cosmos DB change feed; Redis/Orleans state. CRDTs are rarely the right answer in a single-region PG shop. |
| **8. Outbox / transactional messaging** | You must "write to the DB **and** publish a message" and both must happen or neither. Removes the need for a lock or a distributed transaction spanning DB + broker. | Only one system is being written. Or the message must be visible with zero delay (the relay is asynchronous by construction). | EF Core: insert the outbox row in the same `SaveChangesAsync`; a `BackgroundService` relays to Service Bus. NServiceBus/MassTransit ship this. |
| **9. Leader election / leases** | "Only one instance of this deployment should run the cron job." The single most common real-world use of a distributed lock. | You need *correctness-grade* mutual exclusion. Standard lease implementations explicitly do **not** fence — two leaders can coexist during a partition or GC pause. Make the work idempotent instead. | K8s `coordination.k8s.io` `Lease`; Azure Blob lease (15–60 s or infinite); PG advisory lock; Redis lock. |

---

## The nine approaches

### 1. Database constraints as concurrency control

**What it is.** The database's own declarative constraints enforce the invariant, so there is no lock to take and no
application-level coordination at all. Two writers race; the engine picks a winner; the loser gets a deterministic
SQLSTATE to handle. The invariant holds against *every* writer — your service, another service, a migration, a
developer in `psql` — because it lives in the schema, not in the code path that happens to be running.

#### 1a. UNIQUE and `INSERT ... ON CONFLICT`

PG: *"Unique constraints ensure that the data contained in a column, or a group of columns, is unique among all the
rows in the table."* Note the NULL rule: *"By default, two null values are not considered equal in this comparison.
That means even in the presence of a unique constraint it is possible to store duplicate rows that contain a null
value in at least one of the constrained columns."* (`NULLS NOT DISTINCT` changes this.)

`ON CONFLICT` needs a unique index or constraint to infer as the *arbiter*: the `conflict_target` is resolved by
"unique index inference", or you name it explicitly with `ON CONSTRAINT constraint_name`. `DO NOTHING` makes the
target optional; `DO UPDATE` requires it. The concurrency guarantee, verbatim:

> ON CONFLICT DO UPDATE guarantees an atomic INSERT or UPDATE outcome; provided there is no independent error, one
> of those two outcomes is guaranteed, even under high concurrency. This is also known as *UPSERT* — "UPDATE or
> INSERT".

**How you detect which happened.** `RETURNING` only yields *"rows that were successfully inserted or updated"* — a
row rejected by `DO NOTHING` is **not** returned. So `RETURNING id` coming back empty *is* the signal "someone else
already had it", and rows-affected `= 0` means the same:

```sql
-- "claim this job exactly once"
INSERT INTO job_claims (job_id, claimed_by, claimed_at)
VALUES (@jobId, @instanceId, now())
ON CONFLICT (job_id) DO NOTHING
RETURNING job_id;      -- 0 rows => somebody else claimed it. No lock involved.
```

#### 1b. Partial unique indexes — "unique among *active* rows"

The standard answer to soft deletes and to "one active subscription per customer". PG docs: a partial index
*"enforces uniqueness among the rows that satisfy the index predicate, without constraining those that do not."*
Their example:

```sql
CREATE UNIQUE INDEX tests_success_constraint ON tests (subject, target)
    WHERE success;
```

Applied to the usual .NET shape:

```sql
CREATE UNIQUE INDEX ux_subscription_active
    ON subscriptions (customer_id)
    WHERE deleted_at IS NULL;
```

#### 1c. EXCLUSION constraints — the big under-known one

This is the case where a constraint replaces a lock for a genuinely hard concurrency problem. "No two bookings for
the same room may overlap in time" has no `UNIQUE` formulation, and the naive implementation ("SELECT overlapping
rows, if none then INSERT") is a textbook check-then-act race that people usually "fix" with a distributed lock.
PG solves it declaratively.

The guarantee, verbatim: *"Exclusion constraints ensure that if any two rows are compared on the specified columns
or expressions using the specified operators, at least one of these operator comparisons will return false or
null."* And: *"Adding an exclusion constraint will automatically create an index of the type specified in the
constraint declaration."*

The exact working DDL, from the PG range-types docs — note the `btree_gist` extension is required to put a plain
scalar column (`room`) into a GiST exclusion constraint alongside the range:

```sql
CREATE EXTENSION btree_gist;
CREATE TABLE room_reservation (
    room text,
    during tsrange,
    EXCLUDE USING GIST (room WITH =, during WITH &&)
);

INSERT INTO room_reservation VALUES
    ('123A', '[2010-01-01 14:00, 2010-01-01 15:00)');
INSERT 0 1

INSERT INTO room_reservation VALUES
    ('123A', '[2010-01-01 14:30, 2010-01-01 15:30)');
ERROR:  conflicting key value violates exclusion constraint "room_reservation_room_during_excl"

INSERT INTO room_reservation VALUES
    ('123B', '[2010-01-01 14:30, 2010-01-01 15:30)');
INSERT 0 1
```

Use `tstzrange` rather than `tsrange` in any real system. The `&&` operator is range-overlap; `WITH =` requires
`btree_gist`, which *"provides GiST index operator classes that implement B-tree equivalent behavior"* for `text`,
`uuid`, `int4`, etc. The same extension also adds `<>` support, which enables constraints like PG's own `zoo`
example (`EXCLUDE USING GIST (cage WITH =, animal WITH <>)` — one species per cage).

#### 1d. CHECK constraints and their hard limit

CHECK is *per-row only*. PG states this explicitly, and it matters because people reach for it wrongly:

> PostgreSQL does not support `CHECK` constraints that reference table data other than the new or updated row being
> checked. While a `CHECK` constraint that violates this rule may appear to work in simple tests, it cannot
> guarantee that the database will not reach a state in which the constraint condition is false (due to subsequent
> changes of the other row(s) involved). This would cause a database dump and restore to fail.

So: **CHECK cannot enforce a cross-row invariant.** "At most 5 bookings per customer" is not a CHECK constraint.
That case goes to an EXCLUDE constraint if expressible, otherwise to §6 (SSI) or a real lock.

#### 1e. Foreign keys — and the contention they quietly add

FKs are a constraint like the others, but they are *not* free of locking: a foreign-key check takes a row-level lock
on the referenced parent row. From the PG 9.3 release notes, which introduced the current behaviour:

> `UPDATE`s that do not change any columns referenced in a foreign key now take the new `NO KEY UPDATE` lock mode on
> the row, while foreign key checks use the new `KEY SHARE` lock mode, which does not conflict with `NO KEY UPDATE`.
> So there is no blocking unless a foreign-key column is changed.

PG's definition of that mode: *"A key-shared lock blocks other transactions from performing `DELETE` or any `UPDATE`
that changes the key values, but not other `UPDATE`."* Practical consequence: inserting many children of one hot
parent row is fine, but **updating the parent's key column, or deleting it, serialises against every child insert in
flight** — and this is a common surprise source of deadlocks in a busy .NET service.

#### 1f. The error-handling side — this is what makes it usable

A constraint violation is a specific SQLSTATE, not a generic failure. The ones that matter:

| SQLSTATE | Condition name |
| --- | --- |
| `23505` | `unique_violation` |
| `23P01` | `exclusion_violation` |
| `23514` | `check_violation` |
| `23503` | `foreign_key_violation` |
| `23502` | `not_null_violation` |
| `40001` | `serialization_failure` |
| `40P01` | `deadlock_detected` |

In Npgsql these arrive as `PostgresException`, which exposes `SqlState` (`string`, always present),
`ConstraintName`, `TableName`, `ColumnName`, `SchemaName`. Constants live in `PostgresErrorCodes`. The whole point
is that you treat the exception as **flow control**, not as an error:

```csharp
try
{
    await db.SaveChangesAsync(ct);
    return BookingResult.Confirmed;
}
catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg && IsExpected(pg))
{
    return pg.SqlState switch
    {
        // someone else won the race — not an error, just an outcome
        PostgresErrorCodes.UniqueViolation    => BookingResult.AlreadyExists,  // "23505"
        // overlapping booking rejected by the EXCLUDE constraint — also an outcome
        PostgresErrorCodes.ExclusionViolation => BookingResult.SlotTaken,      // "23P01"
        _ => throw new UnreachableException()
    };
}

// Match on the constraint name, not just the SQLSTATE: a table usually has more than one
// unique index, and "which invariant did I hit" is what the caller actually needs to know.
static bool IsExpected(PostgresException pg) =>
    (pg.SqlState == PostgresErrorCodes.UniqueViolation
        && pg.ConstraintName == "ux_subscription_active")
 || (pg.SqlState == PostgresErrorCodes.ExclusionViolation
        && pg.ConstraintName == "room_reservation_room_during_excl");
```

Note the shape: the `when` filter decides whether this is a *known* race at a *known* constraint, and only then is
the exception translated into an outcome. Anything else propagates. Matching on `ConstraintName` rather than the
SQLSTATE alone matters because a table usually carries several unique indexes, and "which invariant did I hit" is
exactly what the caller needs to know.

**When to reach for it.** The invariant is a predicate over rows in one PG database; the losing writer can be told
"no" rather than made to wait; and you want the rule enforced against writers you don't control. Prefer this over a
lock whenever it applies — it has no timeout, no TTL, no lease to leak, no clock assumption, and it is correct under
arbitrary concurrency and arbitrary process death.

**When NOT to.** (a) The invariant isn't expressible as a constraint (cross-row aggregates, "sum ≤ limit"). (b) You
must do external work (call a payment API) *before* deciding — a constraint can only reject after you've already
paid. (c) You need to *serialise* the writers, not reject one of them. (d) Conflicts are the common case, so the
exception path becomes the hot path (throwing is expensive, and in EF Core a failed `SaveChanges` leaves the change
tracker needing care).

---

### 2. Optimistic concurrency control (OCC)

**What it is.** Read the row plus a version token; write conditionally on the token being unchanged; check
rows-affected; if zero, someone else got there first — reload and retry, or surface the conflict. No lock is held
between read and write, so "think time" costs nothing. EF Core's own framing: *"optimistic concurrency takes no
locks, but arranges for the data modification to fail on save if the data has changed since it was queried."*

The raw pattern:

```sql
UPDATE accounts
   SET balance = @newBalance, version = version + 1
 WHERE id = @id AND version = @expectedVersion;
-- rows affected = 0  =>  conflict; reload and retry
```

**PostgreSQL: `xmin` as a free version column.** Every PG table has the system column `xmin`: *"The identity
(transaction ID) of the inserting transaction for this row version. (A row version is an individual state of a row;
each update of a row creates a new row version for the same logical row.)"* Because every `UPDATE` produces a new
row version with a new `xmin`, it behaves as an automatically-maintained version token with zero schema cost.

Does it work reliably? **Within a live database, yes**, with caveats worth knowing:

- **Wraparound.** Transaction IDs are 32-bit and the space is circular: *"for every normal XID, there are two
  billion XIDs that are 'older' and two billion that are 'newer'."* PG's own warning: *"it is unwise to depend on
  the uniqueness of transaction IDs over the long term (more than one billion transactions)."* For OCC this is
  effectively harmless — the window between your read and your write is milliseconds, not two billion transactions —
  but it *is* the reason `xmin` is not a durable identity.
- **`VACUUM FREEZE` does not clobber `xmin` on modern PG.** This is the commonly-repeated objection and it is out of
  date: *"In PostgreSQL versions before 9.4, freezing was implemented by actually replacing a row's insertion XID
  with `FrozenTransactionId`, which was visible in the row's `xmin` system column. Newer versions just set a flag
  bit, preserving the row's original `xmin` for possible forensic use."* Rows carried over from a pre-9.4 cluster
  can still show `xmin = 2`.
- **`ctid` is not a substitute.** PG: *"a row's `ctid` will change if it is updated or moved by `VACUUM FULL`.
  Therefore `ctid` should not be used as a row identifier."*

**EF Core.** Two mechanisms, and the distinction matters:

```csharp
// Database-generated token. SQL Server: maps to `rowversion`. PostgreSQL/Npgsql: maps to `xmin` (must be uint).
public class Person
{
    public int PersonId { get; set; }
    public string FirstName { get; set; }

    [Timestamp]                 // fluent equivalent: .Property(p => p.Version).IsRowVersion()
    public uint Version { get; set; }   // byte[] on SQL Server; uint on PostgreSQL
}

// Application-managed token — you assign it on every save.
public class Document
{
    public int Id { get; set; }
    [ConcurrencyCheck]          // fluent equivalent: .Property(d => d.Version).IsConcurrencyToken()
    public Guid Version { get; set; }
}
```

EF Core then emits `UPDATE [People] SET [FirstName] = @p0 WHERE [PersonId] = @p1 AND [Version] = @p2;` and, per the
docs, *"if a concurrent update occurred, the UPDATE fails to find any matching rows and reports that zero were
affected. As a result, EF Core's `SaveChanges()` throws a `DbUpdateConcurrencyException`."* The same exception is
thrown for a concurrently-modified *delete*; it is **not** thrown for inserts (those surface a provider-specific
unique-violation instead — i.e. §1).

The resolution loop, per Microsoft's own sample shape:

```csharp
var saved = false;
while (!saved)
{
    try { await context.SaveChangesAsync(); saved = true; }
    catch (DbUpdateConcurrencyException ex)
    {
        foreach (var entry in ex.Entries)
        {
            var databaseValues = await entry.GetDatabaseValuesAsync();
            // ... merge entry.CurrentValues against databaseValues ...
            entry.OriginalValues.SetValues(databaseValues); // refresh token to bypass next check
        }
    }
}
```

**SQL Server `rowversion` vs PG's approach.** `rowversion` is a real, indexable `byte[]` column that SQL Server
bumps on every row change; `[Timestamp]` maps to it. `xmin` is a *system* column — nothing added to your schema, no
migration, but it is a `uint`, is not indexable as a user column, and does not survive a dump/restore or a logical
replication re-insert (the new row gets a new inserting XID). EF Core's docs are explicit that this is
provider-specific: *"the details on setting up an automatically-updating concurrency token differ across databases,
and some databases don't support these at all (e.g. SQLite)."*

**HTTP `ETag` / `If-Match` — the same idea over the wire.** RFC 9110: an entity-tag is *"an opaque validator for
differentiating between multiple representations of the same resource"*, and `If-Match` *"makes the request method
conditional on the recipient origin server having a current representation of the target resource that matches the
value(s) given by the field"*. On failure the server responds `412 (Precondition Failed)` and *"MUST NOT perform the
requested method"*. The RFC names the exact problem it solves: *"the 'lost update' problem, wherein a client GET's a
resource's state, modifies it, and PUT's it back to the origin server, only to discover that another client has
modified the state in the meantime."* Return your OCC token as the `ETag`; require `If-Match` on `PUT`/`PATCH`;
translate `DbUpdateConcurrencyException` into `412`.

**When to reach for it.** Read-modify-write on rows you already have; **low contention**; a human or a long-running
step sits between read and write. If you were about to hold a transaction open across a user's editing session,
OCC is the answer instead.

**When NOT to.** High contention. Under heavy conflict every writer retries, each retry re-reads and re-computes,
and throughput collapses while CPU rises — a retry storm. Above a certain conflict rate, a real lock (which queues)
outperforms OCC (which spins). Also avoid when the work between read and write has side effects that can't be
safely re-run, and don't use OCC to protect an invariant that a constraint could enforce outright (§1 is stronger).

---

### 3. Idempotency keys

**What it is.** The client generates a unique key per logical operation and sends it with the request. The server
records `key → (status, response body)` and, on any replay of the same key, returns the stored response instead of
re-executing. Stripe's public API is the canonical published design.

**Stripe's exact semantics** (from Stripe's own API docs):

- **Header:** `Idempotency-Key`. *"All `POST` requests accept idempotency keys. Don't send idempotency keys in `GET`
  and `DELETE` requests because it has no effect. These requests are idempotent by definition."*
- **What is stored:** *"Stripe's idempotency works by saving the resulting status code and body of the first request
  made for any given idempotency key, regardless of whether it succeeds or fails. Subsequent requests with the same
  key return the same result, including `500` errors."*
- **Retention:** *"You can remove keys from the system automatically after they're at least 24 hours old. We
  generate a new request if a key is reused after the original is pruned."* Elsewhere Stripe states keys expire out
  of the system after 24 hours.
- **Same key, different body:** *"The idempotency layer compares incoming parameters to those of the original
  request and errors if they're not the same to prevent accidental misuse."* And: *"Sending the same idempotency
  with different parameters produces an error indicating that the new request didn't match the original."*
- **Concurrent replay:** Stripe's status-code table lists **`409 Conflict` — "The request conflicts with another
  request (perhaps due to using the same idempotent key)."** Stripe also notes: *"We save results only after the
  execution of an endpoint begins. If incoming parameters fail validation, or the request conflicts with another
  request that's executing concurrently, we don't save the idempotent result... You can retry these requests."*
- **Detecting a replay:** *"To identify a previously executed response that's being replayed from the server, look
  for the header `Idempotent-Replayed: true`."*
- **Key format:** *"we suggest using V4 UUIDs, or another random string with enough entropy to avoid collisions.
  Idempotency keys are up to 255 characters long. Avoid using sensitive data... as idempotency keys."*

**Why this is strictly stronger than a lock for external side effects.** A lock gives you mutual exclusion *while
you hold it*. It does not tell you, after a timeout or a crash or an ambiguous network failure, whether the side
effect already happened. A lock cannot answer "did the payment go through?" — and that is exactly the question a
retry needs answered. An idempotency key can, because the record of the attempt is durable and keyed by the caller's
own identifier. Put differently: a lock prevents *concurrent* duplicates; an idempotency key prevents duplicates
**across time, across process restarts, and across ambiguous failures** — which is the failure mode that actually
bites.

**The dedupe-store design, and the check-then-insert race.** The naive implementation is `SELECT` by key, and if
absent, `INSERT` — which is a check-then-act race between two concurrent replays. **The answer is the unique index
itself, not a lock.** Insert first, let the constraint arbitrate (this is §1 doing the work):

```sql
CREATE TABLE idempotency_keys (
    key            text PRIMARY KEY,
    request_hash   bytea       NOT NULL,
    status         text        NOT NULL DEFAULT 'in_progress',  -- in_progress | completed
    response_code  int         NULL,
    response_body  jsonb       NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);

-- Step 1: claim the key. Winner proceeds; loser reads the stored response.
INSERT INTO idempotency_keys (key, request_hash)
VALUES (@key, @hash)
ON CONFLICT (key) DO NOTHING
RETURNING key;         -- 0 rows => replay: SELECT the row and return its stored response,
                       --            or 409 if status is still 'in_progress'.
```

```csharp
var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
    $@"INSERT INTO idempotency_keys (key, request_hash) VALUES ({key}, {hash})
       ON CONFLICT (key) DO NOTHING") == 1;

if (!claimed)
{
    var existing = await db.IdempotencyKeys.SingleAsync(k => k.Key == key, ct);
    if (!existing.RequestHash.SequenceEqual(hash))  return Results.UnprocessableEntity(); // Stripe-style mismatch
    if (existing.Status == "in_progress")           return Results.Conflict();            // Stripe-style 409
    return Results.Json(existing.ResponseBody, statusCode: existing.ResponseCode!.Value);
}
```

**When to reach for it.** Any operation with a non-reversible external side effect that a client may retry: payments,
outbound emails/SMS, calls to a partner API, "create order". Also the standard answer for at-least-once message
consumers — the outbox (§8) makes duplicates certain, so consumers must be idempotent.

**When NOT to.** Pure internal state changes with no external effect — a constraint or OCC is cheaper and needs no
extra table. Also don't bother if you cannot durably record the key *before* performing the work; storing it
afterwards reintroduces exactly the window you were trying to close.

---

### 4. Single-writer / partitioning

**What it is.** Route all work for a given key to exactly one owner, so no two workers ever touch the same state.
Mutual exclusion becomes a property of the topology rather than something acquired at runtime. Nothing is locked
because nothing is shared.

**Kafka partition ordering.** The producer side, verbatim from the Kafka design docs:

> The client controls which partition it publishes messages to. This can be done at random, implementing a kind of
> random load balancing, or it can be done by some semantic partitioning function. We expose the interface for
> semantic partitioning by allowing the user to specify a key to partition by and using this to hash to a partition
> ... For example if the key chosen was a user id then all data for a given user would be sent to the same
> partition.

The Kafka intro states it plainly: *"Events with the same event key (e.g., a customer or vehicle ID) are written to
the same partition"*, and *"Kafka guarantees that any consumer of a given topic-partition will always read that
partition's events in exactly the same order as they were written."*

And the consumer-side half — this is the structural exclusion:

> Our topic is divided into a set of totally ordered partitions, each of which is consumed by exactly one consumer
> within each subscribing consumer group at any given time.

Producer key → one partition; one partition → one consumer in the group. Therefore all events for one account are
handled by one consumer, in order. **That is a mutual-exclusion guarantee with no lock in it.** (Azure Service Bus
sessions give the same shape, and Kafka's newer *share groups* deliberately give this up — "partitions may be
assigned to multiple consumers" — so don't assume the guarantee if you use them.)

**The actor model — Orleans grains in .NET.** Orleans invented the *virtual actor*: *"Actors are purely logical
entities that always exist, virtually. An actor cannot be explicitly created nor destroyed, and its virtual
existence is unaffected by the failure of a server that executes it."* The concurrency guarantee, verbatim:

> Grain activations have a *single-threaded* execution model. By default, they process each request from beginning
> to completion before the next request can begin processing.

Because a grain is addressed by a user-defined key (`IGrainWithStringKey`, `IGrainWithGuidKey`) and the runtime
places exactly one activation per identity, `GetGrain<IAccountGrain>(accountId)` *is* the lock — held by the
runtime, for the duration of one request, without you writing anything. Note the honest caveats Orleans documents:
non-reentrant grains can **deadlock** on call cycles ("Case 2: The calls deadlock"), and marking a grain
`[Reentrant]` keeps execution single-threaded but lets turns from different requests interleave — *"reentrant grains
might see the execution of code for different requests interleaving"* — which reintroduces the interleaving you were
avoiding.

**Consistent hashing** is the routing mechanism underneath both: hash the key to a point on a ring, walk to the next
owner node, so adding or removing a node remaps only a fraction of keys instead of everything. That is what makes
"one owner per key" survivable across scale events. (Kafka uses a simpler hash-to-partition; the partition count is
the thing you cannot change without reshuffling keys.)

**The key insight, stated precisely.** This family **eliminates contention structurally rather than defending
against it**. A lock is a runtime negotiation over shared state; partitioning removes the sharing. The trade is that
you are now bound by your key choice: throughput per key is capped at one worker, and a hot key is a hard ceiling
you cannot lock your way out of.

**When to reach for it.** The work has a natural key you control, and per-key serialisation is exactly the invariant
you want. If you find yourself taking a distributed lock named after an entity ID on every message, you want
partitioning instead.

**When NOT to.** The key isn't known until partway through the operation; a single operation spans multiple keys
(cross-grain/cross-partition transactions are hard and slow); or the key distribution is badly skewed, so one hot
partition becomes the whole system's throughput. Partition count is also sticky — changing it rehashes keys and
breaks ordering across the boundary.

---

### 5. Lock-free / wait-free in-process

**What it is.** Within one process, use atomic hardware instructions and immutable data instead of a monitor. The
canonical primitive is compare-and-swap: `Interlocked.CompareExchange` *"compares two values for equality and, if
they are equal, replaces the first value, as an atomic operation"* and returns *"the original value in
`location1`"* — which is what lets you detect that you lost the race and loop.

**The canonical CAS retry loop in C#:**

```csharp
private long _total;

public long AddAndGet(long delta)
{
    long current, updated;
    do
    {
        current = Volatile.Read(ref _total);
        updated = current + delta;            // arbitrary pure computation goes here
    }
    while (Interlocked.CompareExchange(ref _total, updated, current) != current);
    return updated;
}
```

The `!= current` test is the whole mechanism: `CompareExchange` returns what *was* there, so if it doesn't match
what you read, another thread wrote in between and you recompute. (For a plain add, `Interlocked.Add` is the right
call; the loop earns its keep when the new value is a non-trivial function of the old one, or when swapping a
reference to an immutable object.)

**`System.Collections.Immutable` and persistent data structures.** An immutable collection can be published by a
single atomic reference swap, so readers never see a torn state and never take a lock. The idiom is the CAS loop
above with `ImmutableDictionary<K,V>` or `ImmutableList<T>` in place of the `long`: read the current reference,
build the updated version (structural sharing makes this cheap, not a full copy), `CompareExchange` the reference,
retry if you lost. This is the right shape for read-mostly configuration and routing tables.

**`ConcurrentDictionary` — what is actually lock-free?** Not the whole thing. From the .NET API docs: *"For
modifications and write operations to the dictionary, `ConcurrentDictionary<TKey,TValue>` uses fine-grained locking
to ensure thread safety. (Read operations on the dictionary are performed in a lock-free manner.)"* The
`dotnet/runtime` source confirms the mechanism is **striped locking**, not lock-freedom:

- `Tables._locks` is declared `internal readonly object[] _locks;` with the comment *"A set of locks, each guarding
  a section of the table."*
- `GetBucketAndLock` computes `lockNo = bucketNo % (uint)tables._locks.Length;` — buckets map onto a smaller array
  of locks.
- The default stripe count is `private static int DefaultConcurrencyLevel => Environment.ProcessorCount;`, commented
  *"The number of concurrent writes for which to optimize by default."* The array can grow (`_growLockArray`) up to
  `private const int MaxLockNumber = 1024;`.
- Reads take no lock: the write paths carry comments noting values must be *"written atomically, since lock-free
  reads may be happening concurrently."*

So: **reads are lock-free; writes take one of N bucket locks.** Two writers to different buckets don't contend; two
writers to buckets sharing a stripe do.

**`GetOrAdd`'s factory caveat**, verbatim from the docs:

> However, the `valueFactory` delegate is called outside the locks to avoid the problems that can arise from
> executing unknown code under a lock. Therefore, `GetOrAdd` is not atomic with regards to all other operations on
> the `ConcurrentDictionary<TKey,TValue>` class. ... If you call `GetOrAdd` simultaneously on different threads,
> `valueFactory` may be called multiple times, but only one key/value pair will be added to the dictionary.

Consequence: **the factory must be side-effect-free and cheap**, and you must not assume the object it produced is
the one stored. If the value is expensive or disposable, store a `Lazy<T>` (or `AsyncLazy`) instead — then the
duplicate factory invocations only construct duplicate `Lazy` wrappers, and exactly one `Lazy` wins and runs.

**The ABA problem, and why `Interlocked` alone isn't enough.** Maged Michael's definition (IEEE TPDS 2004, the
hazard-pointers paper):

> It occurs when a thread reads a value A from a shared location, and then other threads change the location to a
> different value, say B, and then back to A again. Later, when the original thread checks the location, e.g., using
> read or CAS, the comparison succeeds, and the thread erroneously proceeds under the assumption that the location
> has not changed since the thread read it earlier. As a result, the thread may corrupt the object or return a wrong
> result.

He adds that it *"affects almost all lock-free algorithms"* and *"was first reported in the documentation of CAS on
the IBM System 370"*. CAS compares *values*, not *histories* — a successful CAS proves the value matches, not that
nothing happened. In managed .NET the usual node-reuse form is largely defanged by the GC (a node still referenced
cannot be recycled underneath you), but Michael is explicit that *"a common misconception is that GC inherently
prevents the ABA problem in all cases"* — moving nodes back and forth between two structures is still ABA-prone
under perfect GC. The practical .NET rule: use `Interlocked` on counters, flags, and reference swaps of immutable
objects; do not hand-roll lock-free linked structures.

**When to reach for it.** Single process; the critical section is a handful of instructions; the shared state is one
memory location or one immutable reference; contention is real enough that a monitor shows up in profiles.

**When NOT to.** The critical section does I/O, allocates heavily, or spans more than one location — CAS is
single-word, and "atomically update these two fields" is not something `Interlocked` gives you. Under very high
contention a CAS loop degenerates into livelock-ish spinning that a lock (which parks the thread) beats. And none of
this crosses process boundaries.

---

### 6. Serializable isolation instead of explicit locks

**What it is.** Ask the database for the strongest isolation level and write the naive code; the engine detects
anomalies and aborts one transaction rather than making you reason about lock order. PostgreSQL implements this as
**SSI (Serializable Snapshot Isolation)**.

**What PG guarantees**, verbatim:

> This level emulates serial transaction execution for all committed transactions; as if transactions had been
> executed one after another, serially, rather than concurrently.

And the framing that makes it valuable — you don't need to know what the *other* transactions do:

> The guarantee that any set of successfully committed concurrent Serializable transactions will have the same
> effect as if they were run one at a time means that if you can demonstrate that a single transaction, as written,
> will do the right thing when run by itself, you can have confidence that it will do the right thing in any mix of
> Serializable transactions, even without any information about what those other transactions might do, or it will
> not successfully commit.

**How it differs from `SELECT ... FOR UPDATE`.** `FOR UPDATE` is pessimistic and blocking: PG says it *"prevents
them from being locked, modified or deleted by other transactions until the current transaction ends"*, and other
transactions attempting to lock those rows *"will be blocked until the current transaction ends"*. Crucially, row
locks only protect **rows that already exist** — they cannot protect against a *phantom* (a row another transaction
is about to insert), which is precisely the "count the bookings, then insert one more" bug. SSI closes that hole
using predicate locks, which are not blocking at all:

> To guarantee true serializability PostgreSQL uses predicate locking, which means that it keeps locks which allow
> it to determine when a write would have had an impact on the result of a previous read from a concurrent
> transaction, had it run first. In PostgreSQL these locks do not cause any blocking and therefore can not play any
> part in causing a deadlock.

They show up as `SIReadLock` in `pg_locks`. And the cost model:

> This monitoring does not introduce any blocking beyond that present in repeatable read, but there is some overhead
> to the monitoring, and detection of the conditions which could cause a serialization anomaly will trigger a
> serialization failure.

**The practical cost: you MUST have retry logic.** PG is unambiguous — *"Applications using this level must be
prepared to retry transactions due to serialization failures."* The error is `ERROR: could not serialize access due
to read/write dependencies among transactions`, SQLSTATE **`40001`**, and PG notes *"it will be very hard to predict
exactly which transactions might contribute to the read/write dependencies and need to be rolled back."* Meaning:
a transaction that did nothing wrong can be aborted. The retry must re-run the *whole* transaction from the start —
you cannot resume it.

```csharp
for (var attempt = 0; ; attempt++)
{
    await using var tx = await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);
    try
    {
        // naive read-then-write logic; no SELECT FOR UPDATE, no advisory lock
        await tx.CommitAsync(ct);
        return;
    }
    catch (PostgresException e) when (
        (e.SqlState is PostgresErrorCodes.SerializationFailure    // "40001"
                    or PostgresErrorCodes.DeadlockDetected)       // "40P01"
        && attempt < 5)
    {
        await tx.RollbackAsync(ct);
        await Task.Delay(TimeSpan.FromMilliseconds(20 * (1 << attempt)), ct); // backoff + jitter
    }
}
```

Two .NET-specific notes: your retry must be *outside* the `DbContext`/connection scope, because an aborted PG
transaction cannot be reused; and EF Core's built-in execution strategies do not retry `40001` for Npgsql by
default — assume you own this loop.

**SQL Server's `SERIALIZABLE` is a different animal.** It is lock-based, not SSI. Microsoft's description: *"Range
locks are placed in the range of key values that match the search conditions of each statement executed in a
transaction. This blocks other transactions from updating or inserting any rows that would qualify... The range
locks are held until the transaction completes. This is the most restrictive of the isolation levels."* So SQL
Server `SERIALIZABLE` **blocks** (and can deadlock); PG `SERIALIZABLE` **aborts**. If you want SSI-like behaviour on
SQL Server, the closest analogue is `SNAPSHOT` isolation, which *"doesn't request locks when reading data"* and
raises an update-conflict error instead. Do not port a retry-on-`40001` design onto SQL Server `SERIALIZABLE` and
expect the same performance profile.

**When to reach for it.** The invariant spans multiple rows or rows that don't exist yet; the logic is complex
enough that hand-placed `FOR UPDATE` locks would be error-prone or deadlock-prone; conflicts are rare; transactions
are short.

**When NOT to.** You can't add a retry loop (batch jobs where re-running is expensive, or code paths with
side effects mid-transaction). High-conflict workloads, where the abort rate makes throughput worse than simply
serialising. Long transactions, which hold predicate locks and inflate the conflict surface. And note PG's own
guidance that if you only need the *old* pre-9.1 "serializable" behaviour, *"Repeatable Read should now be
requested"* — don't pay for SSI you don't need.

---

### 7. Append-only / event sourcing / CRDTs

**What it is.** Stop overwriting. If every write is an *append* of a new immutable fact, two concurrent writers do
not collide — there is no shared cell for them to both assign to. Event sourcing is the systematised form: *"store
the full series of actions taken on an object in an append-only store"*, deriving current state by replaying.

**Why append-only sidesteps write-write conflicts.** Microsoft's Event Sourcing pattern page names the problem
directly — in CRUD, *"because updates require read-modify-write cycles with row-level locking, concurrent writes to
the same entity degrade performance and become a bottleneck under load"* — and the fix: *"Events are immutable, and
you can store them by using an append-only operation... Write throughput improves, especially for the presentation
layer, because append-only writes avoid the row-level lock contention that update-in-place systems create."*

Be precise about what this does and does not buy you. It removes the *write-write* conflict on the row. It does
**not** remove the need to enforce invariants across a stream, and the docs are honest about it: two handlers can
each rehydrate "five seats remaining" and both accept a reservation. *"Event stores address this scenario by using
optimistic concurrency control and reject an append if the stream changed since it was read. Upon rejection, the
handler reloads the entity, reevaluates, and retries."* So append-only relocates the problem into §2 (OCC on a
stream version) rather than dissolving it.

**CRDTs, in one paragraph.** Conflict-free Replicated Data Types solve *convergence without coordination*: replicas
accept writes locally, in any order, while partitioned, and are still guaranteed to converge. From the original
abstract (Shapiro, Preguiça, Baquero, Zawirski, 2011): *"Replicating data under Eventual Consistency (EC) allows any
replica to accept updates without remote synchronisation... Under a formal Strong Eventual Consistency (SEC) model,
we study sufficient conditions for convergence. A data type that satisfies these conditions is called a Conflict-free
Replicated Data Type (CRDT). Replicas of any CRDT are guaranteed to converge in a self-stabilising manner, despite
any number of failures."* The two sufficient conditions are the whole theory:

- **State-based (CvRDT)** — the payload forms a *monotonic semilattice*: merging with a remote state computes the
  least upper bound (`s • m(s′) = s ⊔ s′`), and *"State is monotonically non-decreasing across updates, i.e.,
  s ≤ s • u."* Theorem 1: any such object is SEC.
- **Op-based (CmRDT)** — *"a sufficient condition for convergence of an op-based object is that all its concurrent
  operations commute."* Theorem 2, given causal delivery.

The paper's own concrete example is the counter: a vector of per-replica integers where *"an update `inc(i)`
increments the payload entry at index `i`"*, merge takes *"the per-index maximum"*, and the value is the sum
`|v| = Σⱼ v[j]` — this is the increment-only counter, commonly called a **G-Counter**. Add a second such vector for
decrements and read `|I| − |D|` and you have a PN-Counter. Note the paper is also explicit that this weakens what
you can assert: *"SEC is incomparable to sequential consistency"* — the final state after concurrent add/remove may
be one no serial execution could produce.

**Be honest about over-engineering.** For a single-region .NET/PostgreSQL service, CRDTs are almost always the wrong
tool: you have a single authoritative store, so you can just use a constraint (§1) or a transaction. CRDTs earn
their keep when you genuinely cannot coordinate — multi-region active-active, offline-first mobile clients,
collaborative editing. Event sourcing has a lower bar but Microsoft's own warning is worth quoting: *"Event sourcing
is a complex pattern that introduces significant trade-offs... For most systems and most parts of a system,
traditional data management is sufficient."* Their explicit "not suitable when" list includes straightforward CRUD,
short-lived systems, teams without event-driven experience, and anywhere real-time consistent views are required.

**When to reach for it.** Writes are naturally facts, not overwrites. You need an audit trail or time-travel for
independent reasons. Write contention on a single hot entity is your actual measured bottleneck. (CRDT
specifically: replicas must accept writes while partitioned.)

**When NOT to.** You mostly serve current-state reads; eventual consistency is unacceptable; the team hasn't done it
before; or you're reaching for it *only* to avoid a lock — a unique index is a far cheaper way to avoid the same
lock. Also note the compliance edge: an immutable store conflicts with "right to be forgotten" and needs
crypto-shredding or out-of-store personal data designed in from day one.

---

### 8. The outbox pattern / transactional messaging

**What it is.** You need "save the order **and** publish `OrderCreated`" to be all-or-nothing across two systems — a
database and a message broker. The outbox pattern makes it one transaction: write the business row and an outbox row
in the *same* database transaction, then let a separate relay read the outbox and publish. *"This pattern saves
events in a data store that's typically in an outbox table in your database before it pushes them to a message
broker. When you save the business object and its events within the same database transaction, the system guarantees
no data loss. The transaction either commits everything or rolls back everything if an error occurs. To publish the
events, a separate service or worker process queries the outbox table for unhandled entries, publishes them, and
marks them as processed."*

**The dual-write problem it solves.** The naive code commits to the DB, then publishes. Microsoft's own pseudo-code
and failure list: the publish can fail from a *network error*, a *message service outage*, or a *host failure*, and
then *"the system can't publish the `OrderCreated` event to the message bus, and other services aren't notified that
an order was created... Lost events can cause data inconsistencies across the application."* Reversing the order
just moves the hole — now you can publish an event for an order that never committed.

**How it removes the need for a lock across two systems.** The alternative to an outbox is a distributed
transaction (2PC/MSDTC) or an ad-hoc lock held across both the DB write and the broker publish. Both are ways of
extending a coordination window across a network boundary — and both fail badly when the process dies mid-window,
leaving either a stuck lock or an in-doubt transaction. The outbox collapses the two writes into **one local ACID
transaction plus an at-least-once retry**, so there is no window to protect and no coordinator to fail. The
consistency you get is not "atomic across both systems" but "eventually, exactly the committed facts are published"
— which is almost always what was actually wanted.

```csharp
// One EF Core transaction. Either both rows land or neither does. No lock, no 2PC.
db.Orders.Add(order);
db.OutboxMessages.Add(new OutboxMessage {
    Id = Guid.NewGuid(),
    Type = nameof(OrderCreated),
    Payload = JsonSerializer.Serialize(new OrderCreated(order.Id)),
    OccurredAt = DateTimeOffset.UtcNow
});
await db.SaveChangesAsync(ct);
// A BackgroundService polls OutboxMessages WHERE processed_at IS NULL, publishes, marks processed.
```

**Costs to state up front.** Delivery is **at-least-once**, so consumers must be idempotent (§3) — that is not
optional, it is the defining property of the pattern. Ordering needs deliberate work: *"You must preserve event
order so that the system publishes an `OrderCreated` event before an `OrderUpdated` event"* (partition/session keys,
per-aggregate sequence numbers). And the relay itself is usually a "only one instance should do this" job, which
routes you to §9 — or better, to a `FOR UPDATE SKIP LOCKED` claim so multiple relays can run safely.

**When to reach for it.** Any time a single request must both change your data and tell someone else. If you are
contemplating a distributed transaction, or a lock held across a DB write and an HTTP/broker call, use an outbox.

**When NOT to.** Only one system is being written (just use a transaction). The message must be visible with
sub-millisecond latency (the relay is a poll or a change-feed and adds delay). Or the "message" is really a
synchronous query — an outbox is for facts you're announcing, not for questions you're asking. Note also that
NServiceBus and MassTransit both ship an outbox implementation; hand-rolling is rarely worth it.

---

### 9. Leader election / leases ("only one instance should do this")

**What it is.** N replicas of a deployment, one job that must not run N times. A lease is a time-bounded,
renewable claim: one instance acquires it, renews it while it works, and if it dies the lease expires and someone
else takes over. This is the single most common real-world use of a distributed lock — and the one where the safety
claim is most often overstated.

**Kubernetes `Lease` (`coordination.k8s.io`).** The docs describe Leases as a mechanism *"to lock shared resources
and coordinate activity"*, used internally so that *"only one instance of a component is running at any given
time... like `kube-controller-manager` and `kube-scheduler` in HA configurations, where only one instance of the
component should be actively running while the other instances are on stand-by."* And explicitly for your own code:

> Your own workload can define its own use of Leases. For example, you might run a custom controller where a primary
> or leader member performs operations that its peers do not. You define a Lease so that the controller replicas can
> select or elect a leader, using the Kubernetes API for coordination.

Naming guidance: name it after the component (`example-foo`), and if multiple instances can be deployed, add a
prefix or a hash of the Deployment name to avoid collisions.

**`client-go`'s `tools/leaderelection` is the standard implementation** — and its own package documentation is
where the honest warning lives:

> This implementation does not guarantee that only one client is acting as a leader (a.k.a. fencing).

On clocks: a client bases decisions on *locally* captured timestamps rather than trusting the timestamps in the
leader-election record; the implementation tolerates arbitrary clock **skew** but not arbitrary skew **rate**, and
you tune that by the ratio of `LeaseDuration` to `RenewDeadline` (their example: to tolerate nodes running twice as
fast, use `LeaseDuration` 60 s and `RenewDeadline` 30 s). Tolerance to skew rate *varies inversely with
availability* — a safer margin means slower failover.

**So: efficiency, not correctness.** Your suspicion is right, and the primary source says so directly. Two leaders
can coexist: leader A stalls in a GC pause or is partitioned from the API server, its lease expires, B takes over,
then A wakes up still believing it is leader and completes its write. Nothing in the Lease object stops A's write
from landing. Note the one adjacent guarantee that is *not* the same thing: the Kubernetes coordinated-leader-election
docs say the control plane *"guarantees that only one candidate successfully acquires the Lease"* via optimistic
concurrency on `resourceVersion` — that is exclusivity of **acquisition**, not exclusivity of **action over time**.
The correct design is therefore: use the lease to keep the job from running N times in the *normal* case, and make
the job itself safe if it runs twice — via a constraint (§1), an idempotency key (§3), or a fencing token checked at
the point of write.

**Options, and how they compare.**

| Mechanism | Fit | Notes |
| --- | --- | --- |
| K8s `Lease` + `client-go` leaderelection | Go controllers, and any workload already on K8s | Standard, well-understood, explicitly not fencing. .NET needs a client library or hand-rolled `PATCH` against the API. |
| etcd / ZooKeeper | You already run one for other reasons | Real consensus underneath; still leases at the edge, so still not fencing without a token. |
| Database-backed (PG) | **Best default for a .NET/PG shop** | Either an advisory lock (session-scoped, released on disconnect) or a `leases` table with `holder`, `expires_at` claimed via a conditional `UPDATE`. No new infrastructure; uses the DB's clock, not N pod clocks. |
| Azure Blob lease | Azure-native, no DB needed | *"A lease on a blob provides exclusive write and delete access to the blob."* Duration *"can be between 15 and 60 seconds, or an infinite duration."* Writes without the lease ID fail `412 – Precondition failed`. This is a genuine fencing mechanism **for writes to that blob** — it does not fence anything else you do. |
| Redis lock | Low latency, already in the stack | Weakest safety story under failover; see the Redlock discussion in `03-redlock-debate.md`. |

A PG-backed lease, whole thing:

```sql
CREATE TABLE leases (
    name       text PRIMARY KEY,
    holder     text        NOT NULL,
    expires_at timestamptz NOT NULL
);

-- Acquire or renew, atomically, in one statement. now() is the DB's clock, not the pod's.
INSERT INTO leases (name, holder, expires_at)
VALUES (@name, @instanceId, now() + interval '30 seconds')
ON CONFLICT (name) DO UPDATE
   SET holder = EXCLUDED.holder, expires_at = EXCLUDED.expires_at
 WHERE leases.holder = EXCLUDED.holder     -- I already hold it: renew
    OR leases.expires_at < now()           -- it lapsed: take over
RETURNING holder;   -- 0 rows => someone else holds a live lease
```

That is §1 doing the arbitration again — no lock manager, one round trip, and the expiry decision uses a single
clock. It is still not fencing.

**When to reach for it.** A scheduled/background job in a deployment with N > 1 replicas where running it twice is
*wasteful* (double the API calls, double the log noise, a duplicate report) but not *corrupting*. Also for
active/standby components where failover speed matters more than strict exclusivity.

**When NOT to.** When running twice would corrupt data or double-charge someone. In that case the lease is not the
safety mechanism — the constraint, the idempotency key, or a fencing token checked at the write is. Also avoid
tuning lease durations aggressively short in pursuit of fast failover: shorter leases mean more spurious handovers
and *more* overlap, not less.

---

## How these map to a decision tree

Start from "do I need a lock?" and answer these in order. The order matters — each question routes you to a cheaper,
stronger mechanism than the one below it.

**Q1. Can the database state the rule?**
If the invariant is "only one X" → `UNIQUE` (or a partial unique index if it's "only one *active* X"). If it's "no
two overlapping X" → `EXCLUDE USING gist` with `btree_gist`. If yes → **§1. Stop here.** Handle `23505`/`23P01` as
flow control. This is the branch most .NET/PG teams skip, and it is the one that most often makes the lock
unnecessary. Note the two disqualifiers: a CHECK constraint cannot see other rows, and no constraint can help if you
must do external work before deciding.

**Q2. Does the operation have an external, non-reversible side effect?**
Charge, email, partner API call. If yes → **§3, and §3 is not optional.** A lock cannot tell a retry whether the
side effect already happened; only a durable key→response record can. Build the dedupe store with a unique index and
`ON CONFLICT DO NOTHING` (Q1 doing the work again), never `SELECT`-then-`INSERT`.

**Q3. Am I writing to my database *and* to a broker/another service in the same request?**
If yes → **§8.** This is a dual-write, and neither a lock nor a distributed transaction is the right fix. Insert the
outbox row in the same `SaveChangesAsync`. Then loop back to Q2, because the relay guarantees at-least-once.

**Q4. Is it a read-modify-write on rows that already exist, at low contention?**
If yes → **§2.** On PG, use `xmin` via `IsRowVersion()` on a `uint` — free, no migration. Across an API boundary,
expose it as an `ETag` and require `If-Match`. If there's a human or a long-running step between read and write,
this is definitively the answer over any lock, because holding a lock across think time is the actual bug.

**Q5. Does the invariant span rows that don't exist yet?**
"No more than N bookings", "sum of allocations ≤ budget", anything with a phantom. `SELECT FOR UPDATE` **cannot**
protect this — it locks rows, and the offending row hasn't been inserted. If an `EXCLUDE` constraint can't express
it → **§6 (PG `SERIALIZABLE`)**, and you must write the `40001` retry loop before you ship. Remember SQL Server's
`SERIALIZABLE` blocks rather than aborts — don't port the design across.

**Q6. Am I locking on an entity ID on every single message or request?**
If the lock name is basically `$"lock:order:{orderId}"` → **§4.** You don't want a lock, you want partitioning:
Kafka/Service Bus key, or an Orleans grain keyed by that ID. Contention disappears structurally. Accept the trade:
per-key throughput is now one worker, and hot keys become your ceiling.

**Q7. Is this all inside one process?**
If yes, no distributed anything is needed. Counter/flag/reference swap → **§5** (`Interlocked`, immutable +
`CompareExchange`). Shared read-mostly map → `ConcurrentDictionary` (reads lock-free, writes striped), and remember
`GetOrAdd`'s factory can run more than once — wrap expensive values in `Lazy<T>`. Anything more elaborate than a
single-word swap: just take a `Lock`. Hand-rolled lock-free structures are where ABA lives.

**Q8. Is it "only one replica should run this job"?**
→ **§9**, with eyes open. Use a PG-backed lease (`ON CONFLICT DO UPDATE ... WHERE expires_at < now()`) or Azure Blob
lease. Then assume it will occasionally run twice anyway, and make the *work* safe via Q1 or Q2. If the answer to
"what if it runs twice?" is "we double-charge someone", the lease is not your safety mechanism.

**Q9. Is write contention on one hot entity the measured bottleneck?**
Only if you have the numbers → consider **§7** (append-only / event sourcing). CRDTs only if you genuinely cannot
coordinate — multi-region active-active or offline-first. In a single-region .NET/PG service, this branch is almost
always over-engineering, and the honest move is to say so.

**If you got here**, you have a genuine need for mutual exclusion across processes, over state the database can't
describe declaratively, where rejection isn't acceptable and duplication isn't safe. That is a real distributed lock
— and it's the case the rest of this framework is about. It's also much rarer than the number of distributed locks
in a typical codebase suggests.

---

## Sources

**PostgreSQL (primary — official docs)**
- https://www.postgresql.org/docs/current/ddl-constraints.html — CHECK/UNIQUE/EXCLUDE/FK definitions; the "CHECK
  cannot reference other rows" limitation; NULL handling and `NULLS NOT DISTINCT`; "at least one of these operator
  comparisons will return false or null".
- https://www.postgresql.org/docs/current/rangetypes.html — "Constraints on Ranges"; the verbatim `reservation` and
  `room_reservation` DDL with `EXCLUDE USING GIST (room WITH =, during WITH &&)` and `CREATE EXTENSION btree_gist`.
- https://www.postgresql.org/docs/current/btree-gist.html — what `btree_gist` provides (GiST operator classes with
  B-tree behaviour for scalar types), `<>` support, the `zoo` exclusion-constraint example.
- https://www.postgresql.org/docs/current/indexes-partial.html — partial unique indexes; "enforces uniqueness among
  the rows that satisfy the index predicate"; the `tests_success_constraint` example.
- https://www.postgresql.org/docs/current/sql-insert.html — `ON CONFLICT` syntax, unique index inference, the
  "guarantees an atomic INSERT or UPDATE outcome... even under high concurrency" quote, `RETURNING` semantics.
- https://www.postgresql.org/docs/current/errcodes-appendix.html — SQLSTATE table: `23505`, `23P01`, `23514`,
  `23503`, `23502`, `40001`, `40P01`, `55P03`.
- https://www.postgresql.org/docs/current/transaction-iso.html — SSI: the serial-execution guarantee, the
  "must be prepared to retry" requirement, predicate locks / `SIReadLock`, the non-blocking monitoring statement,
  the Repeatable Read guidance.
- https://www.postgresql.org/docs/current/explicit-locking.html — `FOR UPDATE` / `FOR NO KEY UPDATE` / `FOR SHARE` /
  `FOR KEY SHARE` definitions and blocking behaviour.
- https://www.postgresql.org/docs/release/9.3.0/ — release notes confirming "foreign key checks use the new KEY
  SHARE lock mode".
- https://www.postgresql.org/docs/current/ddl-system-columns.html — `xmin` definition; `ctid` warning; the 32-bit
  XID / "unwise to depend on uniqueness... more than one billion transactions" note.
- https://www.postgresql.org/docs/current/routine-vacuuming.html — wraparound; "Newer versions just set a flag bit,
  preserving the row's original `xmin`"; `FrozenTransactionId`; `vacuum_freeze_min_age`; `autovacuum_freeze_max_age`.

**.NET / Microsoft (primary — Microsoft Learn and dotnet/runtime source)**
- https://learn.microsoft.com/en-us/ef/core/saving/concurrency — EF Core optimistic concurrency: `[Timestamp]`,
  `IsRowVersion()`, `[ConcurrencyCheck]`, `IsConcurrencyToken()`, the generated `UPDATE ... WHERE`,
  `DbUpdateConcurrencyException`, `ex.Entries`, `GetDatabaseValuesAsync()`, `OriginalValues.SetValues()`, and the
  note that inserts throw provider-specific exceptions instead.
- https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked.compareexchange — "Compares two values
  for equality and, if they are equal, replaces the first value, as an atomic operation"; returns "The original
  value in `location1`".
- https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd —
  fine-grained locking for writes, lock-free reads, factory called outside the locks, "valueFactory may be called
  multiple times, but only one key/value pair will be added".
- https://github.com/dotnet/runtime — `src/libraries/System.Private.CoreLib/src/System/Collections/Concurrent/ConcurrentDictionary.cs`:
  `Tables._locks` ("A set of locks, each guarding a section of the table"), `GetBucketAndLock`'s
  `lockNo = bucketNo % _locks.Length`, `DefaultConcurrencyLevel => Environment.ProcessorCount`, `MaxLockNumber = 1024`,
  and the "lock-free reads may be happening concurrently" comments.
- https://learn.microsoft.com/en-us/dotnet/orleans/overview — the virtual actor model; "Actors are purely logical
  entities that always exist, virtually."
- https://learn.microsoft.com/en-us/dotnet/orleans/grains/request-scheduling — "Grain activations have a
  single-threaded execution model"; deadlock cases; `[Reentrant]` / `AlwaysInterleave` / `MayInterleave` semantics.
- https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql — SQL Server
  `SERIALIZABLE` range locks ("the most restrictive of the isolation levels"), and `SNAPSHOT` row versioning.
- https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob — "A lease on a blob provides exclusive
  write and delete access"; 15–60 s or infinite; lease ID required for writes; `412` without it.
- https://learn.microsoft.com/en-us/azure/architecture/patterns/event-sourcing — append-only store; the write
  contention framing; optimistic concurrency on the stream; the "complex pattern... for most systems traditional
  data management is sufficient" warning; the "when not to use" list; at-least-once / idempotent consumers.
- https://learn.microsoft.com/en-us/azure/architecture/databases/guide/transactional-out-box-cosmos — Transactional
  Outbox: the dual-write failure list, "saves the business object and its events within the same database
  transaction", the relay worker, ordering requirements. *(Cosmos-specific article; the pattern description is
  general and it is Microsoft's canonical page for it.)*

**Npgsql (primary — provider docs)**
- https://www.npgsql.org/efcore/modeling/concurrency.html — `xmin` as a concurrency token; `[Timestamp]` /
  `IsRowVersion()` on a `uint` property.
- https://www.npgsql.org/doc/api/Npgsql.PostgresException.html — `SqlState` (string, always present),
  `ConstraintName`, `TableName`, `ColumnName`, `SchemaName`; `PostgresErrorCodes` constants.

**Stripe (primary — official API docs)**
- https://docs.stripe.com/api/idempotent_requests — `Idempotency-Key`; saves status code and body of the first
  request "regardless of whether it succeeds or fails"; 24-hour retention; 255-char limit; UUID v4 guidance;
  "compares incoming parameters to those of the original request and errors if they're not the same"; results saved
  only after execution begins; POST only.
- https://docs.stripe.com/error-low-level — the idempotency section; `409 Conflict` = "The request conflicts with
  another request (perhaps due to using the same idempotent key)"; `Idempotent-Replayed: true`;
  `Stripe-Should-Retry`; the 4xx/5xx caching caveats.

**Apache Kafka (primary — apache/kafka docs source)**
- `docs/design/design.md` (apache/kafka, trunk) — "The client controls which partition it publishes messages to...
  by allowing the user to specify a key to partition by and using this to hash to a partition"; "Our topic is
  divided into a set of totally ordered partitions, each of which is consumed by exactly one consumer within each
  subscribing consumer group at any given time"; share-group differences.
- `docs/getting-started/introduction.md` (apache/kafka, trunk) — "Events with the same event key... are written to
  the same partition"; "Kafka guarantees that any consumer of a given topic-partition will always read that
  partition's events in exactly the same order as they were written."

**Kubernetes (primary — kubernetes.io and pkg.go.dev)**
- https://kubernetes.io/docs/concepts/architecture/leases/ — `coordination.k8s.io` Leases; node heartbeats; leader
  election for control-plane components; the "your own workload can define its own use of Leases" paragraph; naming
  guidance.
- https://pkg.go.dev/k8s.io/client-go/tools/leaderelection — **"This implementation does not guarantee that only one
  client is acting as a leader (a.k.a. fencing)."** Clock-skew tolerance and the `LeaseDuration`/`RenewDeadline`
  ratio.
- https://kubernetes.io/docs/concepts/cluster-administration/coordinated-leader-election/ — `LeaseCandidate` API;
  the "only one candidate successfully acquires the Lease" statement (acquisition exclusivity via `resourceVersion`
  optimistic concurrency).

**IETF / academic (primary)**
- https://www.rfc-editor.org/rfc/rfc9110 §8.8.3, §13.1.1 — ETag as "an opaque validator"; `If-Match` semantics;
  "MUST NOT perform the requested method if it returns a 412 (Precondition Failed)"; the "lost update" paragraph.
- Shapiro, Preguiça, Baquero, Zawirski, *Conflict-free Replicated Data Types*, SSS 2011 (INRIA RR-7687) —
  https://www.lip6.fr/Marc.Shapiro/papers/2011/CRDTs_SSS-2011.pdf — abstract; Definition 4 (monotonic semilattice);
  Theorem 1 (CvRDT); Definition 6 + Theorem 2 (CmRDT, commutativity); §4.1 integer vectors and counters; §3.3 "SEC
  is incomparable to sequential consistency".
- Maged M. Michael, *Hazard Pointers: Safe Memory Reclamation for Lock-Free Objects*, IEEE TPDS 15(6), 2004 —
  §2.3 "The ABA problem": the verbatim definition, "first reported in the documentation of CAS on the IBM System
  370", and the GC misconception note.

**Secondary / cross-reference (clearly labelled)**
- `01-locking/research/03-redlock-debate.md` (this repo) — for the Redis-lock safety argument referenced in §9.
- `01-locking/research/02-pg-advisory-locks-and-pgbouncer.md` (this repo) — for the PG advisory lock option in §9.

---

## Unverified / open

- **`xmin` survival across maintenance operations.** I confirmed from PG docs that `VACUUM`/freezing preserves
  `xmin` on 9.4+. I did **not** verify what `VACUUM FULL`, `CLUSTER`, or `pg_repack` do to `xmin` when they rewrite
  the heap — they may freeze tuples as part of the rewrite. Also unverified: whether logical replication or a
  `pg_dump`/restore preserves it (reasoning says no — restored rows are fresh inserts with a new inserting XID — but
  I did not find a doc statement saying so). If OCC correctness across a maintenance window matters, test it.
- **The `ForNpgsqlUseXminAsConcurrencyToken()` API name** appears in older material and community answers. The
  *current* Npgsql doc shows only `[Timestamp]` / `.IsRowVersion()` on a `uint`. I did not confirm whether the older
  method is obsolete, removed, or still an alias. Use the documented form.
- **EF Core execution strategy and `40001`.** I asserted that EF Core's built-in retrying execution strategies do
  not retry PG serialization failures by default. I did not verify this against the Npgsql `EnableRetryOnFailure`
  implementation or its default transient-error list. Check before relying on it either way.
- **Kafka's older "Guarantees" bullet list.** The frequently-quoted sentence "Messages sent by a producer to a
  particular topic partition will be appended in the order they are sent" does not appear in the current
  `docs/design/design.md` on trunk — the section appears to have been reorganised. The guarantees I quoted above
  *are* in the current source. Don't quote the old sentence as current.
- **CRDT names.** The 2011 SSS paper describes an "increment-only integer counter" and a two-vector
  increment/decrement counter; it does **not** use the names *G-Counter* or *PN-Counter*, and I did not find an
  LWW-Register definition in this paper (it is in the companion tech report, *A comprehensive study of CRDTs*,
  RR-7506, which I did not fetch). Attribute the names to common usage, not to this paper.
- **`Lock` vs `Monitor` for the "just take a lock" advice in Q7** — see `01-locking/research/01-dotnet9-system-threading-lock.md`;
  I did not re-verify the .NET 9 `System.Threading.Lock` details here.
- **Orleans grain single-threading across silo failover.** The single-threaded guarantee is per *activation*. I did
  not verify what happens during a split-brain or a membership change where two silos could briefly both host an
  activation of the same grain identity. Orleans 9's "Strong-Consistency Grain Directory" suggests this was a real
  concern worth checking before treating a grain as a correctness-grade mutex.
- **Azure Blob lease as a fencing mechanism.** I stated that the lease ID fences *writes to that blob* (writes
  without the lease ID fail 412 — this is documented). I did not verify any claim beyond that; it does not fence
  work the leaseholder does elsewhere.
- **Exclusion constraint performance at scale.** I did not research the write-throughput or index-size cost of GiST
  exclusion constraints on a large, hot booking table. Benchmark before adopting one on a high-write path.
