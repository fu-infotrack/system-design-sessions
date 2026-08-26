# Locking demos

Ten demos, no `.csproj`, no `.sln`. Each demo is one `.cs` file you can put
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

Demos 1–3 need nothing. The rest need containers:

```sh
aspire run                       # Postgres 18.3 + PgBouncer 1.25.2 + Redis 8
```

Host ports are pinned high (55432 / 56432 / 56379) so they can't collide with
anything already on your machine. Then run each demo by hand, when you reach
it in the talk:

```sh
dotnet run 01-counter.cs
dotnet run 02-async-lock.cs
./03-mutex-scope.sh                        # 4 scenarios, ~2 min
dotnet run 04-pg-advisory.cs
dotnet run 04b-pgbouncer-leak.cs
dotnet run 05-pg-skip-locked.cs            # add -- --block for the contrast
dotnet run 06-redis-lock.cs                # add -- --naive to break it
dotnet run 07-expiry.cs                    # add -- --fence to fix it
dotnet run 08-no-lock.cs                   # constraints instead of locks
```

## What each one shows

| File | § | The point |
|---|---|---|
| `01-counter.cs` | 1 | A race **doesn't always lose** — it runs 5×, and sometimes gets the right answer. Also: `Lock` is not measurably faster than `Monitor` here. |
| `02-async-lock.cs` | 1.2 | `new SemaphoreSlim(1)` — a stray `Release()` silently raises the limit to 2. Proven live. |
| `03-mutex-scope.sh` | 1b | A named `Mutex` is scoped to the **POSIX session**, not the machine. 4 scenarios, and the surprising cell is "container sharing /tmp". |
| `04-pg-advisory.cs` | 3.3 | Session vs transaction scope — and the same leak via **Npgsql's own pool**, no PgBouncer needed. |
| `04b-pgbouncer-leak.cs` | 3.4 | Through PgBouncer: a second request is told it acquired a lock the first still holds. |
| `05-pg-skip-locked.cs` | 3.2 | `SKIP LOCKED` as a queue: 412 ms vs 1506 ms, zero duplicates either way. |
| `06-redis-lock.cs` | 4.1 | Why unlock must be compare-and-delete. `--naive` deletes someone else's lock, live. |
| `07-expiry.cs` | 4 | **The money shot.** A by-the-book lock, and the invariant breaks anyway. `--fence` shows the fix. |
| `08-no-lock.cs` | Framework §1 | 8 racers → 8 charges unguarded, → 1 row with a unique index, → 1 booking with `EXCLUDE`. The answer is usually not a lock. |

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
- **PgBouncer runs `POOL_MODE=transaction`, `DEFAULT_POOL_SIZE=1`.** Pool size
  1 is what makes `04b` deterministic: both clients land on the same backend,
  so you see the silent violation rather than a hang. Both are the same bug.
- `dotnet publish 03-mutex-b.cs -o out` works on file-based apps — that's how
  the container half of the mutex demo runs.
