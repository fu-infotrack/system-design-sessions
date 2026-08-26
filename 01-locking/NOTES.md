# Locking — speaker's notes

> **This is the deep version — all sections, all mechanisms, ~80 minutes of
> material.** The talk actually runs 25 minutes: see [`TALK.md`](TALK.md) for
> the run sheet, and [`FRAMEWORK.md`](FRAMEWORK.md) for the artifact the
> session exists to teach.
>
> Kept in full because it's the source material behind both, and because the
> per-mechanism detail is worth having when someone asks a question the run
> sheet doesn't cover.

**Audience:** engineers, mixed seniority, .NET / Postgres / Redis / Azure
**Length:** 80 min (60-min cut marked below)
**Format:** talk + live demo + argument

---

## The thesis

> A lock is never the goal. The goal is protecting an invariant.
> Locking is one of about six ways to do that, and it's the one that
> degrades worst as scope widens.

Everything in the session hangs off that. The arc is a ladder — one thread,
one process, one machine, one database, one fleet — and at each rung the
guarantees get weaker and the failure modes get stranger. The last section
is the payoff: most people who reach for a distributed lock actually needed
idempotency.

---

## Section 0 — Framing (5 min)

Open cold with broken code. No slides yet.

```csharp
// This runs on 3 pods. Find the bug.
var order = await _db.Orders.FindAsync(id);
if (order.Status == OrderStatus.Pending)
{
    await _payments.ChargeAsync(order);     // ← customer charged twice
    order.Status = OrderStatus.Paid;
    await _db.SaveChangesAsync();
}
```

Let the room shout "you need a lock." Then park it — we come back to this
exact snippet in Section 5 and fix it *without* a lock.

**The three questions to ask before writing any lock:**

1. **What invariant am I protecting?** ("Only charge once" is an invariant.
   "Two threads shouldn't run this method" is not — it's a symptom.)
2. **What's the blast radius?** One process? One machine? The fleet? The
   answer picks your rung on the ladder.
3. **What happens if the lock is lost mid-work?** In-process: impossible.
   Distributed: guaranteed, eventually. This question is the whole session.

---

## Section 1 — In-process (15 min)

*Verified against docs + Roslyn source + local experiment — see `research/01`, `06`, `07`.*

### The toolbox

| Tool | Reentrant | Async | Notes |
|---|---|---|---|
| `lock` / `Monitor` | yes | **no** | thread-affine; can't `await` inside |
| `System.Threading.Lock` (.NET 9) | yes | no | new type; `lock` lowers to `EnterScope()` |
| `SemaphoreSlim(1,1)` | **no** | **yes** | the async mutex; release from any thread |
| `ReaderWriterLockSlim` | opt-in | no | often slower than `lock`; measure first |
| `Interlocked` | n/a | n/a | free when the invariant is one word |
| `Lazy<T>` | n/a | n/a | the lock you didn't have to write |
| named `Mutex` | yes | no | cross-process — but see §1b, the scope isn't what you think |

### 1.1 `lock` and the new `Lock` type

`lock` is `Monitor.Enter`/`Exit` in a `try/finally`. Reentrant, thread-affine.

.NET 9 added `System.Threading.Lock`, and `lock (x)` on it lowers to
`EnterScope()` returning a disposable `ref struct` instead of calling
`Monitor`. **The trap is a correctness bug, not a performance one:**

> If you hold the same `Lock` instance in a variable typed `object`, `lock`
> falls back to `Monitor` — and the two mechanisms **do not mutually
> exclude each other.** One code path taking `Monitor`, another taking
> `EnterScope`, on the same object: both enter. Confirmed by experiment.

The compiler does warn, but not where you'd expect: **CS9216 fires on the
*conversion*** (`Lock` → `object`), not on the `lock` statement. So the
warning appears at the assignment, possibly in a different file from the
bug. Worth showing.

**Don't repeat the "~25% faster" claim.** It has no Microsoft source — not
in Toub's .NET 9 performance post, not in What's New. It traces back to a
third-party backport's README. Say "cheaper, because it skips the object
header and sync block" and leave the number out.

⚠️ **Microsoft Learn is wrong about this.** The lock-semantics page says
CS9217 = "lock on `System.Threading.Lock` cannot be used in async methods."
In Roslyn, CS9217 is `ERR_RefLocalAcrossAwait` — checked on `main` and
three C#-13-era release branches. Reality: `lock (myLock)` inside an
`async` method **compiles fine** if the body has no `await`; add one and
you get plain **CS1996**. Good moment to make the point that primary
sources beat docs, and docs beat memory.

### 1.2 `SemaphoreSlim` — the async one, and its two footguns

- **Not reentrant.** A recursive call self-deadlocks. `lock` would've been
  fine here — this is a trade, not an upgrade. (Demo 2.)
- **Not thread-affine.** `Release()` from a thread that never waited is
  legal. Feature and footgun.

**The one nobody knows** — and it's the best 60 seconds in §1:

```csharp
var s = new SemaphoreSlim(1);      // maxCount defaults to int.MaxValue
await s.WaitAsync();
s.Release();
s.Release();                       // no exception. limit is now 2.
```

Over-release throws `SemaphoreFullException` (**not**
`SemaphoreMaxCountExceededException` — that type doesn't exist), but only
when it exceeds `maxCount`, and the one-arg constructor sets `maxCount` to
`int.MaxValue`. So a stray `Release()` in a `finally` silently raises your
concurrency limit and nothing ever tells you.

**Always `new SemaphoreSlim(1, 1)`.** That's the takeaway; the two-arg form
turns a silent corruption into a thrown exception.

### 1.3 The rest, briefly

- **You cannot `await` inside `lock`.** Explain *why*: `Monitor` ownership
  is bound to the thread, and a continuation can resume on another one.
- **`ConcurrentDictionary.GetOrAdd`'s factory can run more than once.** The
  dictionary is thread-safe; your factory's side effects are not.
- **Never lock on something you don't own:** `lock(this)`, `lock(typeof(X))`,
  `lock("literal")` — string literals are interned process-wide.
- **Deadlock** = lock-order inversion. One rule: a global lock order.
  `Monitor.TryEnter` with a timeout as the pragmatic escape.

### Demo 1 — the counter

`i++` across 8 threads → wrong, every time. `Interlocked.Increment` →
correct. `lock` → correct, and slower under contention.

### Demo 2 — the async self-deadlock

`SemaphoreSlim` guarding a method that calls itself. Hangs forever.

---

## Section 1b — Crossing the process boundary (6 min)

*Everything here was verified by experiment — see `research/07`. Several
widely-repeated claims turned out to be wrong.*

A named `Mutex` is an OS object; two separate *processes* opening
`new Mutex(false, "OneTrueLock")` contend. `03-mutex-a.cs` +
`03-mutex-b.cs` show it in ten seconds.

### The scope is not the machine

This is the correction worth the whole sub-section. On Unix, .NET backs
named mutexes with **files under `/tmp`**, and an unprefixed name is scoped
to the **POSIX session** (`getsid`) — *not* the machine:

```
/tmp/.dotnet/shm/session3616043/OneTrueLock     <- the sid is in the path
```

So a "single instance of this app" guard built on a named `Mutex` **silently
fails across systemd units and across SSH sessions.** That's a real
production bug, not a curiosity.

On Windows the analogous rule is the `Global\` vs `Local\` prefix and
per-session isolation.

### The demo — one script, four scenarios

`./03-mutex-scope.sh` runs the Linux half. **Every row below was reproduced
on this machine** — Linux/WSL2 and, via interop, real Windows:

| Holder | Contender | Name | Result |
|---|---|---|---|
| Linux | Linux, **same** POSIX session | unprefixed | **BLOCKED** |
| Linux | Linux, **different** POSIX session | unprefixed | acquired — no contention |
| Linux | Linux, different POSIX session | `Global\` | **BLOCKED** |
| Linux | container, `/tmp` **not** shared | unprefixed | acquired — no contention |
| Linux | container, `/tmp` shared | unprefixed | acquired — no contention |
| Linux | container, `/tmp` shared | `Global\` | **BLOCKED** |
| Windows | Windows, same session | unprefixed | **BLOCKED** |
| Windows | WSL | unprefixed | acquired — no contention |
| Windows | WSL | `Global\` | acquired — no contention |

Because on Unix .NET backs named mutexes with *files*:

```
unprefixed  ->  /tmp/.dotnet/shm/session<sid>/<name>
Global\     ->  /tmp/.dotnet/shm/global/<hash>
```

The demo prints the actual paths, so you can point at
`/tmp/.dotnet/shm/session3616043/OneTrueLock` on screen and let the room
read the session id out of the filename. That lands harder than any slide.

**The counter-intuitive cell is row 2, column 1.** Sharing `/tmp` is *not
enough*: a container's PID 1 is session 1, so it looks in `session1/` and
finds nothing. Crossing the container boundary takes **both** a shared
`/tmp` **and** a `Global\` name. Ask the room to predict that cell before
you run it — nearly everyone gets it wrong, including, it turns out,
confident-sounding research.

### Windows is the same trap by a different mechanism

Windows named mutexes are **kernel objects**, not files. But the prefix rule
is the same shape: unprefixed means `Local\`, which is scoped to the
**Terminal Services session**.

The practical consequence, which the docs imply but don't spell out: a
Windows **service runs in session 0** and an interactive user in session 1+.
So a service and a desktop app using the same unprefixed name **do not
contend** — structurally the identical bug to the Unix one, for a completely
different reason. Creating a `Global\` object on Windows also generally needs
`SeCreateGlobalPrivilege`.

### And the one that will actually bite this team

**A WSL process and a Windows process never share a named mutex — not even
with `Global\`.** Verified both ways. The implementations have nothing in
common (kernel objects vs `/tmp` files), and WSL2 is a separate VM anyway.

So if anyone develops in WSL and deploys to Windows, or the reverse:
*"it worked on my machine"* carries **zero** information about the locking
behaviour of the deployed thing. Worth saying out loud to a room that does
exactly this.

**"Cross-process" is not "cross-machine", on Unix it isn't even
"cross-session", and across the WSL boundary it is nothing at all.** A lock's
scope is the scope of the thing implementing it. That sentence sets up the
entire second half of the talk.

### Abandonment is unreliable here too

`AbandonedMutexException` is supposed to tell the next waiter "you now hold
a lock over state someone left half-written." On Linux, when the crashed
process was the *only* holder, the backing file is reinitialised and **the
exception is silently lost** — reproduced both ways. So the one safety
feature that would tell you about a crashed holder is the one you can't
rely on cross-platform. Segue straight into §2.

---

## Section 2 — The jump (10 min) ★ *the conceptual centre*

**The shift in one line:** in-process locks are *guaranteed*; distributed
locks are *advisory and time-bounded*.

A distributed lock needs a TTL, because the holder might die and nobody can
tell the difference between "dead" and "slow". And the moment it has a TTL,
**it can expire while you are still working.** Everything weird about
distributed locking descends from that one fact.

Things that can put you past your TTL without you noticing:

- a stop-the-world GC pause
- the VM being descheduled by the hypervisor
- a network partition between you and the lock store
- a slow downstream call you forgot had no timeout
- someone attaching a debugger

### Kleppmann's split — use this framing all session

|  | **Efficiency** | **Correctness** |
|---|---|---|
| Why you're locking | avoid duplicate work | duplicate work corrupts data |
| Cost of a double-run | wasted CPU / money | wrong data, double charge |
| Is a lock enough? | yes, sloppy is fine | **no** — need fencing or idempotency |

Get the room to sort their own real use cases into these two columns. This
is the exercise that makes the session stick.

---

## Section 3 — Postgres (18 min) ★ *the practical core*

Postgres is the spine of this section. Most of the room can reach for these
today without adding infrastructure — which is the argument. **The database
you already have is a better lock than the Redis you'd have to add.**

### 3.1 Row locks — `SELECT ... FOR UPDATE`

- Held to end of transaction. **No TTL, no expiry.** The connection dying
  rolls it back. This is a genuine, underrated advantage over Redis: there
  is no clock, so there is no clock to be wrong.
- Requires the row to *exist*. You cannot lock "order 12345" before insert.
- `NOWAIT` → error immediately if taken. `SKIP LOCKED` → take the next one.
- `FOR NO KEY UPDATE` / `FOR SHARE` — mention in passing; the weaker modes
  exist and FK checks take them implicitly.

### 3.2 `SKIP LOCKED` is a work queue — give it its own slide

```sql
SELECT id, payload FROM jobs
WHERE status = 'pending'
ORDER BY id
FOR UPDATE SKIP LOCKED
LIMIT 1;
```

N workers, no coordinator, no lock service, no duplicate delivery.

`05-pg-skip-locked.cs` measures it — 12 jobs, 4 workers:

| | wall clock | duplicates |
|---|---|---|
| `FOR UPDATE SKIP LOCKED` | **412 ms** | 0 |
| plain `FOR UPDATE` | 1506 ms | 0 |

Both correct. The plain version is 3.6x slower purely because the workers
queue behind each other instead of taking different rows. This is the
Competing Consumer session in one keyword — plant the flag and tell them
it's the follow-up.

### 3.3 Advisory locks — lock an arbitrary key, no row required

*Verified live on PG 17.11 + PgBouncer 1.25.2 — see `research/02`.*

The escape hatch for "I need to serialise on something that isn't a row":
an order not yet inserted, a tenant-wide import, a cache rebuild.

| | Scope | Released by |
|---|---|---|
| `pg_advisory_lock(k)` | **session** | explicit unlock, or disconnect |
| `pg_advisory_xact_lock(k)` | **transaction** | commit or rollback, always |

Also `pg_try_advisory_lock` (non-blocking), the `_shared` variants,
`pg_advisory_unlock_all`.

**Timeouts — correction to the folklore.** People say advisory locks have
no timeout because the function takes no timeout argument. Both
`lock_timeout` **and** `statement_timeout` do in fact apply to advisory
lock waits — measured, both scopes, both firing on the deadline. The docs
never use the word "advisory", which is why the myth persists.
(`LockAcquire → WaitOnLock → ProcSleep`.)

**Key space.** One `bigint`, or two `int4`s. The two forms are **disjoint**
— confirmed in source (`field4` = 1 vs 2) and by measurement — so
`pg_advisory_lock(1)` and `pg_advisory_lock(0,1)` never collide.

The collision that *does* bite: the common `pg_advisory_lock(hashtext(key))`
idiom. **`hashtext()` returns `integer`, not `bigint`** — so you get 2³², not
2⁶⁴, and by the birthday bound you're at coin-flip odds of a collision at
about **77,000 keys**. Two unrelated keys silently serialising, surfacing
as latency nobody can explain. Use the two-int form and namespace it.

### 3.4 The pooling leak — demo this, it's the best gotcha in the talk

**Default to `pg_advisory_xact_lock`.** Two reasons; the second is the one
that reaches production.

1. Session locks are *stackable* — acquire twice, unlock twice. Easy to
   leak by one.
2. Session locks ride the pooled connection, and **under EF Core you do not
   own the connection — the pool does.**

`10-efcore-pooling.cs`. No PgBouncer, no exotic configuration: EF Core on
Npgsql's pool, which is on by default.

```
request 1: took pg_advisory_lock(101)
           connection state right after: Closed      <-- already back in the pool
request 1: DbContext disposed
   server still holds it: 1
request 2: pg_try_advisory_lock(101) -> TRUE
           ^ two requests, same lock, both told they hold it.
```

**The detail that makes this worse than it looks:** EF Core closes the
connection *immediately after the statement*. You don't have to reach the end
of the request or dispose the `DbContext` — a single `ExecuteSqlRaw` is enough
to hand a lock-holding connection back to the pool.

Explicitly opening the connection doesn't save you (scenario 2) — it changes
*when* it leaks, not *whether*.

Scenario 3 is the fix: `pg_advisory_xact_lock` inside an explicit
`BeginTransactionAsync()` → released on commit, every time.

**The rule, in one line:**

> `pg_advisory_xact_lock` inside an explicit transaction. Never
> `pg_advisory_lock`.

A session-scoped advisory lock is bound to the **connection**. Your
`DbContext` is scoped to a request; the connection underneath it is not.

> **If you ever adopt PgBouncer**, the same bug exists one layer lower and is
> harder to see — its own feature matrix lists `Session-level advisory locks |
> Yes | Never`, and `server_reset_query` still *reads* as `DISCARD ALL` via
> `SHOW CONFIG` in transaction mode while never running. Details in
> [`research/02`](research/02-pg-advisory-locks-and-pgbouncer.md). Not
> presented — we don't run it.

### 3.4b Make the locks visible

`pg_locks` is what makes this section land. Two psql tabs and a third
showing the lock table:

```sql
SELECT locktype, classid, objid, objsubid, pid, granted
FROM pg_locks WHERE locktype = 'advisory';
```

Session lock in tab 1 → visible in tab 3 → end tab 1's transaction → **still
there**. Repeat with `xact` → gone on commit. That contrast is the sub-section.

### 3.5 The locks you don't write

- **Unique index.** `INSERT ... ON CONFLICT DO NOTHING` is frequently the
  correct answer to "make sure this happens once", and it costs nothing.
- **Optimistic concurrency.** `UPDATE ... SET v = v+1 WHERE id=@id AND v=@v`,
  check rows-affected, retry. No waiting, no deadlock, no lock to leak.
  Right answer whenever contention is low — which is most of the time.
- **Isolation levels.** A lot of "I need a lock" is really "I need
  `SERIALIZABLE`" or "I need to stop doing read-then-write". Name the
  option so people know it exists; don't teach MVCC today.

> SQL Server equivalents → **Appendix A**. Point at it, don't present it,
> unless the room asks.

---

## Section 4 — Redis and friends (15 min)

*Sources verified — see `research/03`, `research/04`, `research/05`.*

### 4.1 The single-instance lock, done properly

```
SET lock:order:123 <random-token> NX PX 30000
```

Unlock is **not** `DEL`. It's a compare-and-delete — historically a Lua
script, and since **Redis 8.4** a first-class command:

```lua
-- the classic, still what most clients ship
if redis.call("get", KEYS[1]) == ARGV[1]
  then return redis.call("del", KEYS[1]) else return 0 end
```
```
DELEX lock:order:123 IFEQ <random-token>     -- Redis 8.4+
```

**Why the token matters.** Walk this slowly — it's the best three minutes
of the section:

1. A takes the lock, TTL 30s
2. A stalls 35s (GC pause)
3. Lock expires. B takes it.
4. A wakes, finishes, calls `DEL` — **deletes B's lock**
5. C takes it. B and C both running. Invariant gone.

Plain `DEL` doesn't merely fail to protect you; it actively breaks the
*next* holder. Demo this live with `06-redis-lock.cs --naive`.

Also worth saying: on a single instance, failover to a replica loses the
lock — replication is async.

### 4.2 Renewal / watchdog

A background timer extending the TTL while work is in flight (Redisson's
model). Buys a lot in practice. Does **not** make it correct — a partition
stops the renewal and the work at different moments.

### 4.3 Redlock — the debate ★

The algorithm: N independent masters (no replication between them), same
key and random value on each, you own it only with a **majority** *and* the
whole round trip finished inside the TTL:

```
MIN_VALIDITY = TTL - (T2 - T1) - CLOCK_DRIFT
```

**Present the agreement first, then the fork.** The received framing —
"Kleppmann vs antirez, who won" — is the boring and slightly false version.
They agree on the central fact, both in print:

> Once the TTL elapses, mutual exclusion is gone — and this is true of
> **every** auto-releasing distributed lock, ZooKeeper included.
> *antirez:* "this problem is common with all the distributed locks
> implementations." *Kleppmann:* "no matter what lock service you use."

Then the three real forks:

| | Kleppmann | antirez |
|---|---|---|
| **Fencing tokens** | the remedy: monotonic number, resource rejects stale | circular — a resource that can reject a stale token is *already* a linearizable store, so why did you need the lock? |
| **Network delay** | safety must not depend on bounded delay | steps 1 & 3 re-check elapsed time, so slow network only *shrinks* validity, never fakes it |
| **Crash + restart** | node forgets it granted a lock; 3-of-5 becomes lockable twice | delayed restart — hold a crashed node out longer than max TTL |

**Kleppmann's killer diagram**, worth drawing by hand: Client 1 takes a
30s lease → GC stops the world for 40s → lease expires → Client 2 takes it
and writes → Client 1 wakes and writes on top. And you *cannot* fix it by
checking the clock before the write, because the GC can pause you between
the check and the write.

**The line to steal for the whole talk:** his charge isn't that Redlock has
a bug. It's that Redlock's *safety* depends on timing — and safety
properties are not supposed to depend on timing. Only liveness is.

**Where it partly resolved in public:** redis.io's own docs now say, in
Redis's voice, "You should implement fencing tokens… applies to any
distributed locking system", and acknowledge "Redis is not using monotonic
clock for TTL expiration mechanism." The official page links both posts.
That's a nicer ending than "nobody won."

### 4.4 Fencing tokens, precisely

Monotonically increasing number issued with the lock; **the resource**
remembers the highest it has seen and rejects anything lower. The
load-bearing words are *the resource* — it has to participate. That is
simultaneously the whole idea and the whole objection, because most real
resources are "send this email", "call this API", "move this physical
thing." You cannot fence those. Which is why the honest answer is usually
idempotency — §5.

### 4.5 The alternatives table

| Mechanism | Release on crash | Fences the resource? | Notes |
|---|---|---|---|
| Redis `SET NX PX` | TTL | no | fast, everywhere, weakest |
| Redlock | TTL | no | contested; complexity ↑ |
| PG advisory (xact) | on disconnect | no | **no TTL to get wrong** |
| PG `UPDATE … WHERE version=` | n/a | **yes** | the resource checks — real fencing |
| etcd / ZooKeeper lease | session expiry | yes (revision / zxid) | correct, more ops |
| Azure Blob lease | 15–60s or ∞ (-1) | **for that blob, yes** — see below | underrated for Azure shops |
| DynamoDB conditional write | n/a | yes (version attribute) | it's optimistic concurrency |

**Azure Blob lease — the nuance, since it's the only mainstream service
that does resource-side enforcement out of the box.** A stale writer gets
`409` (someone else re-leased) or `412` (expired/broken) — the *service*
rejects it, which is exactly what Kleppmann says fencing requires. But the
lease ID is a GUID checked for **equality**, not a monotonic ordering
token, so it's closer to antirez's unique-token-plus-check-and-set. Two
caveats worth stating out loud:

- a write with **no** lease ID succeeds once the lease has expired — the
  client has to actually send it
- it fences **that blob only**. Not emails, API calls, or SQL writes.

And the trap for anyone reaching for the library: `DistributedLock.Azure`
uses blob leases, but by default leases a *sentinel* blob — so in default
mode you get mutual exclusion **without** the fencing benefit.

---

## Section 5 — When not to lock (10 min) ★ *the payoff*

Come back to the Section 0 snippet. Fix it four ways, none of them a lock:

1. **Idempotency key** — pass an idempotency key to the payment provider.
   Charge twice, get charged once. *(→ bridges to Session 3)*
2. **Optimistic concurrency** — `UPDATE orders SET status='paid' WHERE
   id=@id AND status='pending'`. Rows affected = 0 means someone beat you.
   The database already had a lock; you just used it properly.
3. **Single writer by partition** — route all work for an order to the same
   consumer (Kafka partition, actor, consistent hash). Contention becomes
   structurally impossible instead of defended against.
4. **Leader election** — for "only one replica should run this cron". This
   is the single most common real-world distributed-lock use, and it wants
   a lease, not a mutex.

### The decision table

| Situation | Reach for |
|---|---|
| One process, shared state | `lock` / `Interlocked` |
| One process, async | `SemaphoreSlim(1,1)` |
| Multiple pods, row exists, short work | `SELECT ... FOR UPDATE` |
| Multiple pods, work queue | `FOR UPDATE SKIP LOCKED` |
| Multiple pods, no row yet | `pg_advisory_xact_lock` |
| Low contention, retry is cheap | optimistic concurrency |
| External side effect | **idempotency key** — not a lock |
| One replica runs the job | leader election / lease |
| You need a lock and Redis is all you have | `SET NX PX` + token + Lua unlock, and know it's efficiency-only |

---

## Section 6 — Wrap (5 min)

Three things to remember:

1. **Name the invariant.** If you can't, you don't need a lock, you need
   to think harder.
2. **Distributed locks expire while you hold them.** Design for it or
   don't use one.
3. **If the side effect leaves your process, you need idempotency, not
   mutual exclusion.**

---

## 60-minute cut

Drop Demo 2, §3.5's isolation-level bullet, and Azure Blob from the §4
table. Trim §3.4 to one contrast instead of two. Do **not** drop Section 2
or Section 5 — they're the session.

---

## Demos — file-based, one script per idea

**Verified locally:** SDK `10.0.302`, `aspire` CLI `13.3.5`, Docker `29.7.2`.
All three present, so this approach is viable on your machine today.

### Why file-based

Every demo is **one `.cs` file you can put on screen whole**. No `.csproj`,
no `.sln`, no folder tree to scroll past before reaching the point. You run
it with `dotnet run 01-counter.cs` and the audience sees the entire program
and its output at once. For a talk this matters more than it does for real
code — the ceremony *is* the noise.

Trade-off to know: file-based apps need the **.NET 10 SDK**. LockPlayground
is .NET 8 and csproj-based, so this is a rewrite of it, not an edit. The
existing repo stays as-is (it still works); this becomes the new demo set.

### Layout

```
01-locking/demos/
  apphost.cs           Aspire AppHost — Postgres + Redis
  01-counter.cs        race → Interlocked → lock          §1
  02-async-lock.cs     lock+await won't compile; SemaphoreSlim traps  §1
  03-mutex-a.cs        named Mutex, holder                §1b
  03-mutex-b.cs        named Mutex, contender             §1b
  03-mutex-scope.sh    driver: 4 scenarios, session + container       §1b
  connection.cs        shared connection strings (#:include)
  04-pg-advisory.cs    session vs xact; pg_locks visibility           §3.3
  10-efcore-pooling.cs EF Core leaks a session advisory lock       §3.4
  05-pg-skip-locked.cs FOR UPDATE SKIP LOCKED as a queue  §3.2
  06-redis-lock.cs     SET NX PX + Lua unlock; --naive shows the DEL bug  §4
  07-expiry.cs         sleep past the TTL → two holders → corruption  §4
```

Twelve files, no projects. `03-mutex-a/b` stay as a pair because the whole
point is two OS processes.

### How they run on stage

```sh
aspire run                    # once, up front — Postgres + Redis
dotnet run 01-counter.cs      # then one of these per section
dotnet run 06-redis-lock.cs --naive
```

Deliberately **not** orchestrating the demos *through* the AppHost. Aspire's
job here is just "give me the two containers and their connection strings";
you want to launch each script by hand, at the moment you talk about it, and
re-run it when someone asks "what if...". A dashboard that auto-starts nine
programs is the wrong shape for a talk.

*(Whether a file-based AppHost can reference file-based apps as resources is
one of the open research items — but even if it can, the above is the better
stage setup.)*

### The two to build first

`10-efcore-pooling.cs` — because it's the finding the room will least expect
and it's their own stack. No PgBouncer, no special configuration: EF Core on
Npgsql's default pool. Set `Maximum Pool Size=1` so the second request
deterministically draws the same connection.

`07-expiry.cs` — same shape as `01-counter.cs`, a shared counter and a race,
except the race is now *across the TTL boundary*. The lock is held, correctly,
by the book, and the invariant breaks anyway. Run it in a loop on screen while
you talk through §4 and let it corrupt live. That's the demo people remember.

### Flags over rebuilds

Give each script a `--flag` for its wrong variant rather than a second file:
`06-redis-lock.cs --naive` uses `DEL` instead of the Lua compare-and-delete,
`07-expiry.cs --ttl 5` shortens the window. Toggling a flag on stage beats
switching files, and the diff stays visible in one screen of code.

### Exact syntax — verified on this box

SDK `10.0.302`, Aspire CLI `13.3.5`, Aspire packages `13.5.3`, Docker `29.7.2`.
A single-file AppHost was confirmed provisioning real `postgres:18.3` +
`redis:8.6` containers and feeding a file-based worker. This isn't inferred
from docs — it ran.

One space after the keyword, `@` for versions, `=` for properties:

```csharp
#!/usr/bin/env -S dotnet --
#:package Npgsql@9.*              // bare name is an error (NU1015) — version required
#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false
#:project ../Lib/Lib.csproj
#:include helpers.cs              // multi-file DOES work, SDK 10.0.300+
#:exclude skip.cs
```

`#:ref` exists but is experimental and gated — don't use it.

**The Aspire AppHost is ONE sdk directive**, not `Microsoft.NET.Sdk` plus
Aspire, and there is no separate `Aspire.Hosting.AppHost` package. `aspire new`
emits and `aspire add` maintains exactly this:

```csharp
#:package Aspire.Hosting.PostgreSQL@13.5.3
#:package Aspire.Hosting.Redis@13.5.3
#:sdk Aspire.AppHost.Sdk@13.5.3
```

Run it with `aspire run`. `builder.AddCSharpApp("worker", "worker/worker.cs")`
can pull other `.cs` demos in as resources — it needs
`#pragma warning disable ASPIRECSHARPAPPS001`.

### Connection strings: pin host ports, don't inject

`AddCSharpApp` works, but for a talk **don't** wire the demos in as AppHost
resources. Pin fixed host ports on the containers and let each script use a
plain `localhost` connection string with an env-var override. Then
`dotnet run 04-pg-advisory.cs` just works after `aspire run`, with no
dashboard round-trip and nothing to re-plumb when someone asks "what if…".

It also sidesteps a sharp edge: Aspire's injected Redis string now carries
`ssl=true`, which will not do what you want against a local container.

### Sharp edges

- **`PublishAot=true` is the default** for file-based apps — kills Hot Reload
  and can break reflection-heavy libraries. Set `#:property PublishAot=false`.
- Aspire runs file-based resources with `--no-cache`, so they rebuild on every
  start. Fine for nine tiny files; surprising if you don't expect it.
- A glob in `#:include` disables build caching.
- Stale `dcp` processes cause confusing JSON-RPC failures from `aspire run`.
  If it misbehaves, kill them first.
- `dotnet project convert app.cs` promotes a script to a real project if a
  demo outgrows the format.

## Discussion prompts

- Where in *our* systems are we locking for efficiency vs correctness?
- Do we have any session-scoped advisory locks behind a pooled connection?
- Which of our cron jobs would double-run if the deployment scaled to 2?
- Where are we locking when an idempotency key would be simpler?

---

## Research — done

All seven questions answered from primary sources, several verified by
live experiment rather than reading. Files in `research/`:

| File | Verified how |
|---|---|
| `01-dotnet9-system-threading-lock.md` | docs + Roslyn source on 4 branches |
| `02-pg-advisory-locks-and-pgbouncer.md` | **live** PG 17.11 + PgBouncer 1.25.2 |
| `03-redlock-debate.md` | both blog posts fetched verbatim |
| `04-redis-canonical-lock.md` | redis.io primary |
| `05-azure-blob-lease.md` | MS Learn REST + SDK reference |
| `06-semaphoreslim-vs-monitor-fairness.md` | docs + dotnet/runtime source |
| `07-named-mutex-on-unix.md` | **live** experiment, incl. Docker + `setsid` |
| `08-dotnet-file-based-apps-and-aspire.md` | **live** end-to-end on this box |

### Claims the research killed

Things I'd have said on stage that turned out to be wrong:

- "`Lock` is ~25% faster" — no Microsoft source; traces to a third-party README.
- "CS9217 stops you locking in an async method" — **Microsoft Learn is wrong**;
  CS9217 is `ERR_RefLocalAcrossAwait`. The real one is CS9216, on the conversion.
- "`SemaphoreMaxCountExceededException`" — doesn't exist. It's `SemaphoreFullException`.
- "Advisory locks have no timeout" — `lock_timeout` and `statement_timeout` both apply.
- "A named `Mutex` is machine-wide" — on Unix it's **POSIX-session**-scoped.
- "The PgBouncer problem is a leaked lock" — it's a *silent mutual-exclusion
  violation*; a second client is told it acquired the lock.
- "Azure blob lease ID is a fencing token" — equality-checked GUID, not monotonic,
  and `DistributedLock.Azure` leases a sentinel blob by default anyway.

That list is itself a good closing slide: **the folklore was wrong seven times
out of seven.**
