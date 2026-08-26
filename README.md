# System design sessions

Talk material for a series of internal knowledge-sharing sessions. Each
session is a markdown outline plus runnable demos.

| # | Session | Status |
|---|---|---|
| 1 | [Locking](01-locking/SKELETON.md) | outline + 9 demos, research complete |
| 2 | Caching | planned |
| 3 | Competing Consumer & Idempotency | planned |
| 4 | Pagination | planned |

## Session 1 — Locking

The arc is a ladder: one thread → one process → one POSIX session → one
machine → one database → the fleet. At every rung the guarantees get weaker
and the failure modes get stranger.

The thesis: **a lock is never the goal — protecting an invariant is.** Most
people who reach for a distributed lock actually needed idempotency.

- **[SKELETON.md](01-locking/SKELETON.md)** — the session outline, ~80 min
- **[demos/](01-locking/demos/)** — nine file-based `.cs` scripts, all verified running
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
