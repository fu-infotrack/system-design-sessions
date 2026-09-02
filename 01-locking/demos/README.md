# Locking demos

Ten demos across twelve files, no `.csproj`, no `.sln`. (`03-mutex-a`,
`03-mutex-b` and `03-mutex-scope.sh` are one demo; `apphost.cs` and
`connection.cs` are shared infrastructure, not demos.)

**Three are used in the 25-minute talk** — `03-mutex-scope.sh`,
`10-efcore-pooling.cs`, `07-expiry.cs` — climbing the scope ladder from one
machine to one database to the fleet. The rest are reference: for self-study,
and for settling arguments. Each demo is one `.cs` file you can put
on screen whole and run with `dotnet run`.

Every output quoted in `../TALK.md`, `../FRAMEWORK.md` and `../NOTES.md` was produced by these scripts on a
real machine — nothing here is illustrative-only.

## Requirements

| | version used |
|---|---|
| .NET SDK | 10.0.302 (file-based apps need 10.0.300+) |
| Aspire CLI | 13.3.5 |
| Docker | 29.7.2 |

## Running

Demos 1 and 2 need nothing. `03-mutex-scope.sh` needs Docker for scenarios 3
and 4 only (not Aspire). The rest need the containers:

```sh
aspire run                       # Postgres 18.3 + Redis 8
```

Host ports are pinned high (55432 / 56379) so they can't collide with
anything already on your machine. Then run each demo by hand, when you reach
it in the talk:

```sh
dotnet run 01-counter.cs
dotnet run 02-async-lock.cs
./03-mutex-scope.sh                        # 4 scenarios, ~2 min
dotnet run 04-pg-advisory.cs
dotnet run 10-efcore-pooling.cs
dotnet run 05-pg-skip-locked.cs            # add -- --block for the contrast
dotnet run 06-redis-lock.cs                # add -- --naive to break it
dotnet run 07-expiry.cs                    # add -- --fence to fix it
dotnet run 08-no-lock.cs                   # reference: constraints instead of locks
dotnet run 09-optimistic.cs                # reference: optimistic concurrency
```

## What each one shows

| File | § | The point |
|---|---|---|
| `01-counter.cs` | 1 | A race **doesn't always lose** — it runs 5×, and sometimes gets the right answer. Also: `Lock` is not measurably faster than `Monitor` here. |
| `02-async-lock.cs` | 1.2 | `new SemaphoreSlim(1)` — a stray `Release()` silently raises the limit to 2. Proven live. |
| `03-mutex-scope.sh` | 1b | A named `Mutex` is scoped to the **POSIX session**, not the machine. 4 scenarios, and the surprising cell is "container sharing /tmp". |
| `04-pg-advisory.cs` | 3.3 | Session vs transaction scope, made visible through `pg_locks`. Session scope isn't a mistake — it's the right tool for leader election on a *dedicated* connection. |
| `10-efcore-pooling.cs` | 3.4 | **Our stack.** EF Core closes the connection right after the statement, so one `ExecuteSqlRaw` leaks a session advisory lock. The next request is *told it acquired* it. |
| `05-pg-skip-locked.cs` | 3.2 | `SKIP LOCKED` as a queue: 412 ms vs 1506 ms, zero duplicates either way. |
| `06-redis-lock.cs` | 4.1 | Why unlock must be compare-and-delete. `--naive` deletes someone else's lock, live. |
| `07-expiry.cs` | 4 | **The money shot.** A by-the-book lock, and the invariant breaks anyway. `--fence` shows the fix. |
| `09-optimistic.cs` | Framework §4 | *Reference.* Lost update; the rows-affected gotcha; retry cost vs contention (2→0.5, 16→7.5 per worker). |
| `08-no-lock.cs` | Framework §4 | *Reference.* 8 racers: 8 charges unguarded, 1 row with a unique index, 1 booking with `EXCLUDE`. | 8 racers → 8 charges unguarded, → 1 row with a unique index, → 1 booking with `EXCLUDE`. The answer is usually not a lock. |

## Flags, not file-switching

Each script carries its own wrong variant behind a flag, so you toggle on
stage instead of opening a second file and losing the room:

```sh
dotnet run 06-redis-lock.cs -- --naive      # DEL instead of compare-and-delete
dotnet run 07-expiry.cs -- --fence          # resource rejects stale writers
dotnet run 07-expiry.cs -- --ttl 5          # widen the lease, lose less often
dotnet run 05-pg-skip-locked.cs -- --block  # plain FOR UPDATE
```

## Notes on the setup

- **`IsProxied = false`** is required to pin host ports. Aspire's default DCP
  proxy assigns random ones, and `WithHostPort` alone does not override it.
- **Redis is a plain `AddContainer`, not `AddRedis`.** Aspire's Redis
  integration defaults to TLS on 6379 plus a generated `--requirepass` —
  correct for a real app, but it stops you inspecting keys with `redis-cli`
  mid-demo.
- **`10-efcore-pooling.cs` pins `Maximum Pool Size=1`.** That's what makes it
  deterministic: the second request is guaranteed to draw the same physical
  connection, so you see the silent violation rather than a hang. Both are the
  same bug.
- `dotnet publish 03-mutex-b.cs -o out` works on file-based apps — that's how
  the container half of the mutex demo runs.
