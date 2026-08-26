# Locking — 25 minute run sheet

**Goal:** the room leaves able to use [`FRAMEWORK.md`](FRAMEWORK.md). Not a tour
of mechanisms — a way of deciding.

**Shape:** three demos, each earning one move in the argument, with the
framework in the middle.

> Deep material, all sections, all mechanisms: [`NOTES.md`](NOTES.md).
> This file is what you actually run.

---

## Timings

| | Section | Min | Cumulative |
|---|---|---|---|
| 1 | The problem | 2.5 | 2:30 |
| 2 | Demo — your defaults are already broken | 3.5 | 6:00 |
| 3 | Demo — and a correct lock isn't enough | 3.5 | 9:30 |
| 4 | **The framework** | 7.5 | 17:00 |
| 5 | Demo — the answer is usually not a lock | 4 | 21:00 |
| 6 | Wrap | 4 | 25:00 |

Running long? Cut §2 to a 30-second mention (it's the least load-bearing).
Never cut §4 or §5.

---

## 1 · The problem — 2.5 min

Open cold. Code on screen, no slides.

```csharp
// This runs on 3 pods. Find the bug.
var order = await _db.Orders.FindAsync(id);
if (order.Status == OrderStatus.Pending)
{
    await _payments.ChargeAsync(order);
    order.Status = OrderStatus.Paid;
    await _db.SaveChangesAsync();
}
```

Let the room say "you need a lock." **Park it** — promise to come back at
21:00 and fix it without one.

Then the thesis, and leave it on screen:

> A lock is never the goal. Protecting an invariant is.

---

## 2 · Demo — your defaults are already broken — 3.5 min

```sh
dotnet run 04-pg-advisory.cs
```

Scroll past the first two scenarios; scenario 3 is the one:

```
request 1: took a SESSION lock on 44
request 1: connection disposed ("finished")
   advisory locks still on the server: 1
request 2: pg_try_advisory_lock(44) -> TRUE
           it was TOLD it acquired the lock. Request 1 still holds it.
```

**Say:** two unrelated requests both believe they hold the same lock. One
machine. No PgBouncer. Npgsql's default settings. `Dispose()` returned the
connection to the pool instead of closing it, the session lock rode along, and
session advisory locks are stackable so re-acquiring *succeeds*.

**The move this earns:** you cannot reason about locking from folklore or from
what the API name implies. That's why the rest of this is a framework and not
a list of tips.

*(Optional, 20s: the same bug at the infra layer is `04b-pgbouncer-leak.cs`.)*

---

## 3 · Demo — and a correct lock isn't enough — 3.5 min

```sh
dotnet run 07-expiry.cs
```

```
expected counter: 8
actual counter:   4
lost updates:     4
```

**Say:** every worker took the lock. `SET NX PX`, unique token,
compare-and-delete unlock — everything the docs tell you to do. Updates were
lost anyway, silently.

Because a distributed lock needs a TTL (the holder might die and you can't
tell "dead" from "slow"), and **the moment it has a TTL it can expire while
you are still working**. A GC pause is enough.

Then, if time allows:

```sh
dotnet run 07-expiry.cs -- --fence
```

3 writes rejected instead of 4 silently lost — because the **resource**
checked a fencing token. Note what that required: the resource had to
participate. You cannot fence an email.

**The move this earns:** "get a lock" is not a complete answer to a
correctness problem.

---

## 4 · The framework — 7.5 min

The heart of the talk. [`FRAMEWORK.md`](FRAMEWORK.md) on screen.

### First: four questions you must answer (3 min)

1. **What invariant am I protecting?** "Only charge once" is an invariant.
   "Two threads shouldn't run this method" is a symptom.
2. **What does a double-run cost?** Wasted money (*efficiency*) or corrupted
   data (*correctness*)? Different answers, different tools.
3. **Where does the side effect land** — inside your store, or outside it?
4. **What's the blast radius?** Threads, machine, or fleet.

Ask the room to sort *their own* current work into question 2's two columns.
This is the bit that makes it stick.

### Then: walk the tree (4.5 min)

Walk it top-down, out loud, and make the ordering the point:

```
1. Can the data store enforce it?          -> no lock
2. Side effect outside the store?          -> idempotency, not a lock
3. Contention structurally avoidable?      -> no lock
4. Blast radius?                           -> picks the family
5. Is the DB the shared state?             -> FOR UPDATE / advisory xact
6. Efficiency or correctness?              -> Redis is fine / is not enough
7. Can the resource reject a stale writer? -> fence, or restructure
```

**The line to land:** most people enter at step 4 — *"I need a distributed
lock, which one?"* — and the whole job of this framework is to make steps 1
through 3 happen first. In practice most of those questions terminate at step
1 or 2.

Call out step 7's dead end explicitly: a framework that always produces an
answer is lying. Sometimes the honest output is *change the design*.

---

## 5 · Demo — the answer is usually not a lock — 4 min

Come back to the opening snippet. Then:

```sh
dotnet run 08-no-lock.cs
```

```
1. check-then-insert, NO constraint
   8 racers -> 8 rows   <-- the customer was charged 8 times

2. unique index + ON CONFLICT DO NOTHING
   8 racers -> 1 row, 1 winner, 7 no-ops

3. EXCLUDE constraint — no overlapping bookings
   8 racers -> 1 booking, 1 winner, 7 rejected 23P01
```

**Say:** same eight concurrent writers every time. The difference isn't the
application code — it's whether the invariant was **written down in the
schema**. And once it is, it binds every writer, including ones that don't
know the rule exists: a migration, another service, someone in psql at 2am.

A lock only binds the code that remembers to take it.

Show the `EXCLUDE` DDL, because most people have never seen it:

```sql
create extension if not exists btree_gist;
create table bookings (
    room_id int not null,
    during  tstzrange not null,
    exclude using gist (room_id with =, during with &&)
);
```

"No two bookings may overlap" — declarative, no lock, no read-then-write.

Then fix the opening snippet: an **idempotency key** on the payment call, and
`UPDATE orders SET status='paid' WHERE id=@id AND status='pending'` with a
rows-affected check. No lock anywhere.

---

## 6 · Wrap — 4 min

**Three things to remember:**

1. **Name the invariant.** If you can't, you don't need a lock — you need to
   think harder.
2. **Distributed locks expire while you hold them.** Design for it or don't
   use one.
3. **If the side effect leaves your process, you need idempotency, not mutual
   exclusion.**

**Then point at the artifacts:** `FRAMEWORK.md` is the thing to bookmark — the
map, the eight questions, the tree, the anti-patterns table.

**Close on the folklore table.** Eight things "everyone knows" about locking,
none of which survived checking against primary sources — including a live
documentation bug on Microsoft Learn. It's a good note to end on because it
argues for the framework better than any assertion could: this is a topic
where intuition and even documentation are unreliable, so use a checklist.

---

## Before you present

```sh
cd demos && aspire run          # wait ~60s for containers
dotnet run 04-pg-advisory.cs    # warm the build cache for all three
dotnet run 07-expiry.cs
dotnet run 08-no-lock.cs
```

File-based apps compile on first run — do it before the room is watching.
`07-expiry.cs` is nondeterministic by nature; run it once to check you're
getting a satisfying number of lost updates, and re-run if it's boring.
