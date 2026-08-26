# System design sessions

Talk material for a series of internal knowledge-sharing sessions. Each
session is a markdown outline plus runnable demos.

| # | Session | Status |
|---|---|---|
| 1 | [Locking](01-locking/FRAMEWORK.md) | framework + 25-min talk + 10 demos |
| 2 | Caching | planned |
| 3 | Competing Consumer & Idempotency | planned — session 1 defers idempotency and `SKIP LOCKED` here |
| 4 | Pagination | planned |

## Session 1 — Locking

A decision framework, not a tour of mechanisms.

The thesis: **a lock is never the goal — protecting an invariant is.**

Most developers enter the problem at "I need a distributed lock, which one?"
The framework's job is to make three questions happen first — can the data
store enforce this itself, does the side effect land outside the store, and
can contention be made structurally impossible. In practice most questions
terminate there, and the answer is a unique constraint or an idempotency key.

The 25-minute talk stays on **locking**. The alternatives aren't presented —
the decision tree routes to them, and Part 4 of the framework is the landing
page. Idempotency is deferred to session 3, which teaches it properly.

- **[FRAMEWORK.md](01-locking/FRAMEWORK.md)** — **start here.** The map, the eight
  questions, the decision tree, the anti-patterns. This is the thing to bookmark.
- **[TALK.md](01-locking/TALK.md)** — 25-minute run sheet: three demos up the scope ladder, then the framework
- **[NOTES.md](01-locking/NOTES.md)** — the deep version, all mechanisms, ~80 min of material
- **[demos/](01-locking/demos/)** — eleven file-based scripts, all verified running
- **[research/](01-locking/research/)** — primary-source notes behind every claim

### Why the research folder exists

Locking is a topic where the folklore is unusually wrong. Eight questions were
researched against primary sources — official docs, Roslyn source, the actual
Kleppmann and antirez posts — and several were verified by standing up real
infrastructure rather than reading about it.

Claims that did not survive:

| Folklore | Reality |
|---|---|
| `System.Threading.Lock` is ~25% faster | No Microsoft source. Traces to a third-party README. |
| CS9217 = "can't lock in an async method" | **Microsoft Learn is wrong.** CS9217 is `ERR_RefLocalAcrossAwait`. |
| `SemaphoreMaxCountExceededException` | Doesn't exist. It's `SemaphoreFullException`. |
| Advisory locks have no timeout | `lock_timeout` and `statement_timeout` both apply. |
| A named `Mutex` is machine-wide | On Unix it's scoped to the **POSIX session**. |
| The PgBouncer problem is a leaked lock | It's a *silent mutual-exclusion violation* — a second client is told it acquired the lock. |
| Azure blob lease ID is a fencing token | Equality-checked GUID, not monotonic. And `DistributedLock.Azure` leases a sentinel blob by default. |

That table is itself the closing slide.

## Running the demos

```sh
cd 01-locking/demos
aspire run                    # Postgres + PgBouncer + Redis
dotnet run 01-counter.cs      # then one per section
```

Needs .NET SDK 10.0.300+, the Aspire CLI, and Docker. See
[demos/README.md](01-locking/demos/README.md).
