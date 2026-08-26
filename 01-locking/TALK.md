# Locking — 25 minute run sheet

**Goal:** the room leaves able to use [`FRAMEWORK.md`](FRAMEWORK.md).

**Scope:** this is a talk about **locking**. The alternatives to locking are
deliberately *not* presented — see [Where the no-lock material goes](#where-the-no-lock-material-goes)
at the bottom for how they still get covered.

**Shape:** three demos that climb the scope ladder — one machine, one
database, the fleet — each one breaking an assumption. Then the framework.

> Deep material, all mechanisms: [`NOTES.md`](NOTES.md).

---

## Timings

| | Section | Min | Cumulative |
|---|---|---|---|
| 1 | The problem | 2.5 | 2:30 |
| 2 | Demo — one machine: the scope isn't what you think | 3.5 | 6:00 |
| 3 | Demo — one database: EF Core leaks your lock | 3.5 | 9:30 |
| 4 | Demo — the fleet: a correct lock isn't enough | 3.5 | 13:00 |
| 5 | **The framework** | 8 | 21:00 |
| 6 | Wrap | 4 | 25:00 |

Running long? Cut §2 — it's the most fun and the least load-bearing.
Never cut §5.

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

Let the room say "you need a lock." **Park it** — you come back at 21:00.

Then the thesis, and leave it up:

> A lock is never the goal. Protecting an invariant is.

Say what the next ten minutes are for: *three demos, three scopes, and in
each one a lock behaves differently from how the API name implies.*

---

## 2 · One machine — 3.5 min

```sh
./03-mutex-scope.sh 1 2          # ~32 seconds
```

A named `Mutex`. Two processes, same name, same user, same machine — and
**no contention**. Then the same thing with a `Global`-prefixed name, and it
blocks.

**Ask the room to predict it before you run it.** Almost nobody gets it.

**Say:** on Unix .NET backs named mutexes with *files*, and an unprefixed
name lands in `/tmp/.dotnet/shm/session<sid>/`. The POSIX session id is
literally in the path. So an unprefixed named `Mutex` is scoped to the
**session**, not the machine — every "only one instance of this app" guard
built this way silently fails across systemd units and SSH logins.

Point at the path on screen and let them read the session id out of it.

**If the room runs Windows** — and they do — add the one-liner that lands:
Windows has the identical trap by a different mechanism. Named mutexes there
are kernel objects, unprefixed means `Local\` = per Terminal Services
session, and **a service runs in session 0 while a desktop app runs in
session 1+**, so they don't contend either.

And the one worth ten seconds on its own: **a WSL process and a Windows
process never share a named mutex, `Global\` included.** If you develop in
WSL and deploy to Windows, "it worked on my machine" carries zero information
about the deployed locking behaviour. Full matrix in `NOTES.md`.

*(Scenarios 3 and 4 do the same across a container boundary — sharing `/tmp`
alone is not enough, you need `Global` too. Run them only if you're ahead.)*

---

## 3 · One database — 3.5 min

```sh
dotnet run 10-efcore-pooling.cs
```

This is our stack: EF Core on Npgsql's pool, which is on by default.

```
request 1: took pg_advisory_lock(101)
           connection state right after: Closed      <-- already back in the pool
request 1: DbContext disposed
   server still holds it: 1
request 2: pg_try_advisory_lock(101) -> TRUE
           ^ two requests, same lock, both told they hold it.
```

**Say:** two unrelated requests both believe they hold the same lock. Default
settings, nothing exotic. And look at the connection state — **EF Core closed
it right after the statement**. You don't have to reach the end of the
request; one `ExecuteSqlRaw` hands a lock-holding connection back to the pool.

Session advisory locks are *stackable*, so when the next request draws that
same connection and re-acquires, it **succeeds**.

No error. No log line.

Scenario 2 kills the obvious workaround: explicitly opening the connection
changes *when* it leaks, not *whether*.

**The rule that falls out**, and it's scenario 3:

> `pg_advisory_xact_lock` inside an explicit transaction. Never
> `pg_advisory_lock`.

A session-scoped advisory lock is bound to the **connection** — and under EF
Core you don't own the connection, the pool does. Your `DbContext` is scoped
to a request; the connection underneath it isn't.

---

## 4 · The fleet — 3.5 min

```sh
dotnet run 07-expiry.cs
```

```
expected counter: 8
actual counter:   4
lost updates:     4
```

**Say:** every worker took the lock. `SET NX PX`, unique token,
compare-and-delete unlock — everything the docs tell you to do. Updates
were lost anyway, silently.

Because a distributed lock needs a TTL — the holder might die and you can't
tell "dead" from "slow" — and **the moment it has a TTL it can expire while
you are still working.** A GC pause is enough.

If you have 40 seconds spare:

```sh
dotnet run 07-expiry.cs -- --fence
```

Three writes rejected instead of four silently lost, because the **resource**
checked a fencing token. Note what that required: the resource had to
participate. You cannot fence an email.

**The move this earns:** "get a lock" is not a complete answer to a
correctness problem.

---

## 5 · The framework — 8 min

The heart of it. [`FRAMEWORK.md`](FRAMEWORK.md) on screen.

### Four questions you must answer (3.5 min)

1. **What invariant am I protecting?** "Only charge once" is an invariant.
   "Two threads shouldn't run this method" is a symptom.
2. **What does a double-run cost?** Wasted money (*efficiency*) or corrupted
   data (*correctness*)? Different answers, different tools.
3. **Where does the side effect land** — inside your store, or outside it?
4. **What's the blast radius?** Threads, machine, or fleet.

Get the room to sort *their own current work* into question 2's two columns.
This is the exercise that makes it stick, and it's worth the minute.

### Walk the tree (4.5 min)

```
1. Can the data store enforce it?          -> no lock
2. Side effect outside the store?          -> idempotency, not a lock
3. Contention structurally avoidable?      -> no lock
4. Blast radius?                           -> picks the family      [demo §2]
5. Is the DB the shared state?             -> FOR UPDATE / xact     [demo §3]
6. Efficiency or correctness?              -> Redis is / is not enough [demo §4]
7. Can the resource reject a stale writer? -> fence, or restructure
```

Tie steps 4, 5 and 6 back to the three demos by name — they were the
evidence for exactly those rows.

**The line to land:** most people enter at step 4 — *"I need a distributed
lock, which one?"* — and the framework's whole job is to make steps 1 to 3
happen first.

Call out step 7's dead end explicitly: a framework that always produces an
answer is lying. Sometimes the honest output is *change the design*.

---

## 6 · Wrap — 4 min

**Three things to remember:**

1. **Name the invariant.** If you can't, you don't need a lock — you need to
   think harder.
2. **Distributed locks expire while you hold them.** Design for it or don't
   use one.
3. **If the side effect leaves your process, you need idempotency, not mutual
   exclusion.**

**And one practical instruction:** if the tree lands you at a distributed
lock, use [madelson/DistributedLock](https://github.com/madelson/DistributedLock)
rather than hand-rolling. Ten providers behind one interface, and its
`HandleLostToken` is the only off-the-shelf answer to *"what if the lock is
lost mid-work?"* — the question §4's demo just showed you can't ignore.

Its own docs make the argument for you, which is worth a slide:

> *"Timeout-based locking approaches such as Redis locks and Azure leases have
> an inherent risk that an extended hang on the machine holding the lock could
> cause the timeout to expire before the lock can be automatically-renewed."*

That's a mainstream .NET library saying what the talk just said. Its
recommended "unified approach" — a Postgres or SQL Server lock protecting
resources in that same database, over one connection and transaction — is
step 5 of the tree, and it's why step 5 comes before step 6.

**Point at the artifact.** `FRAMEWORK.md` is the bookmark: the map, the eight
questions, the tree, Part 5's provider matrix, the anti-patterns table.

**Close on the folklore table.** Eight things "everyone knows" about locking,
none of which survived checking against primary sources — including a live
documentation bug on Microsoft Learn. It argues for using a checklist better
than any assertion could: this is a topic where intuition *and the docs* are
unreliable.

---

## Where the no-lock material goes

You asked how the alternatives get covered if they get no talk time. Three
ways, and none of them need minutes on the clock:

1. **The decision tree covers them.** Steps 1, 2 and 3 route out of locking
   entirely, and each exit names its alternative. A dev doesn't need to have
   sat through a lecture on constraints — they need the tree to hand them the
   right answer at the moment they're deciding. That's the delivery mechanism.
2. **[`FRAMEWORK.md` Part 4](FRAMEWORK.md#part-4--not-locking) is the landing
   page** — nine approaches, each with *use when* / *avoid when*, keyed to the
   tree step that routes there. Plus two reference demos (`08-no-lock.cs`,
   `09-optimistic.cs`) for self-study and for settling arguments.
3. **Session 3 teaches the biggest one properly.** Idempotency is your
   Competing Consumer & Idempotency session — so this talk should *point* at
   it, not pre-empt it. Same for `SKIP LOCKED`, which is the Competing
   Consumer pattern in one keyword.

In the wrap, that's one sentence: *"steps 1 to 3 of the tree point at things
that aren't locks — they're written up in Part 4, and session 3 does
idempotency properly."*

---

## Before you present

```sh
cd demos && aspire run             # wait ~60s
./03-mutex-scope.sh 1 2            # warm all three
dotnet run 10-efcore-pooling.cs
dotnet run 07-expiry.cs
```

File-based apps compile on first run — do it before the room is watching.
`07-expiry.cs` is nondeterministic; run it once and re-run if the number of
lost updates is unimpressive.
