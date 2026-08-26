# Q10 — DistributedLock (madelson): Release and Expiry Semantics, Provider by Provider

*Library version audited: **master @ `8007a86`, release 2.8.3 (2026-07-14)**. Every claim about the library below was
read out of that source tree, not inferred from the docs — where the docs and the source disagree, this file says so.*

## Summary

The ten providers split into **two families and one hybrid, and that split is the only thing you really need to
remember**. Six — SqlServer, Postgres, MySql, Oracle, FileSystem and WaitHandles — are **ownership-scoped**: the lock is
held by a *thing that exists* (a database session, an open OS file handle, a kernel object), and it is released when
that thing goes away. There is **no TTL, no clock, and no renewal**; nothing can decide you have "run out of time"
while you are paused. Three — Redis, Azure blob leases and MongoDB — are **lease-scoped**: a deadline is written down
somewhere, a background task races to extend it before it lapses, and if that race is lost the lock is *transferred
to someone else while you still believe you hold it*. All three default to a **30-second expiry** and auto-extend at
roughly **one third of it** (9s / 10s / 10s respectively). **ZooKeeper is the tenth and sits between the two**: an
ephemeral znode is ownership-scoped, but the ownership is a session with a **20-second negotiated timeout**, so in
effect it expires — with the important difference that no wall clock is consulted anywhere (*"ZooKeeper doesn't use
real time, or clock time, at all"*), the heartbeat is protocol-level rather than application-level, and the quorum
rather than a key TTL decides.

The important correction to the obvious conclusion is this: **ownership-scoped does not mean safe, it means the
failure has a different shape.** A connection-scoped lock has no clock to be wrong, but the server can still delete
your session out from under you — an idle-connection killer, an admin `KILL`, a failover, a TCP keepalive giving up,
or a transaction-mode connection pooler like PgBouncer recycling the backend. The moment that happens the lock is
*genuinely released* and another process can *legitimately* take it, while your process still holds a handle object
that says otherwise. The difference from the TTL family is that this is a **detection-lag** window (bounded by how
fast you notice a dead socket) rather than a **deadline** window (bounded by a clock you do not control and cannot
pause). Detection lag can be driven toward zero; a deadline cannot. That is the whole argument for the left-hand
column of the table below.

One provider breaks the pattern in a way worth knowing: **MongoDB is the only provider in the library that exposes a
fencing token** (`MongoDistributedLockHandle.FencingToken`, a monotonic `long`). It is also the *only* TTL-based
provider that gives you the tool Kleppmann says you need to make TTL-based locking actually safe. Azure comes second,
exposing `LeaseId` — which fences the leased blob itself, and nothing else. Every other provider gives you a boolean
"I have it" and nothing you can hand downstream.

---

## The table

Read the two rightmost columns together. "Can go stale mid-work?" answers *can someone else legitimately hold this
lock while I still think I do*; "Loss detection" answers *and would I find out*.

| Provider | Released by | TTL / lease | Auto-renew | Can go stale mid-work? | Loss detection (`HandleLostToken`) |
| --- | --- | --- | --- | --- | --- |
| **SqlServer** (`sp_getapplock`) | `sp_releaseapplock` on dispose; **session end** (connection close / server-side kill); or **transaction end** if `UseTransaction` | **None.** No expiry exists at any layer | No — but a **keepalive** query runs every **10 min** (`KeepaliveCadence`, default `10:00`) to dodge Azure SQL's idle-connection governor | **Yes, but only via session death** — server `KILL`, idle-connection governor, failover, dropped TCP. No clock can expire it | **Supported.** Active: a `WAITFOR DELAY` sleep query is parked on the connection; its failure + `DbConnection.StateChange` cancels the token. **Not active for externally-owned connections** — passive only |
| **Postgres** (advisory locks) | `pg_advisory_unlock` on dispose; **session end**; or **transaction end** if `UseTransaction` (`pg_advisory_xact_lock`) | **None.** Session-level advisory locks never expire | No. `KeepaliveCadence` defaults to **OFF** (`Timeout.InfiniteTimeSpan`) | **Yes, but only via session death** — `idle_session_timeout`, backend termination, failover, **and PgBouncer transaction pooling, which silently breaks session-scoped advisory locks** | **Supported.** Same mechanism, using `pg_sleep` as the parked query |
| **MySql / MariaDB** (`GET_LOCK`) | `RELEASE_LOCK` on dispose; **session end** | **None.** `GET_LOCK`'s timeout argument is an *acquire* timeout, not a hold expiry | No renewal. Keepalive defaults to **3.5 hours** in source (docs wrongly say OFF — see caveats) to stay under MySQL's 8h `wait_timeout` | **Yes, but only via session death** — `wait_timeout`, `KILL`, failover | **Supported.** Parked query is `SELECT SLEEP(...)` |
| **Oracle** (`DBMS_LOCK`) | `DBMS_LOCK.RELEASE` on dispose; **session end** | **None** for the lock. (`ALLOCATE_UNIQUE`'s `expiration_secs` expires the *name→id mapping*, not your lock — see caveats) | No. `KeepaliveCadence` defaults to **OFF** | **Yes, but only via session death** — profile `IDLE_TIME`, `ALTER SYSTEM KILL SESSION`, failover | **Supported.** Parked query is `DBMS_SESSION.SLEEP`, plus a deliberate `_ = connection.State` poke to force Oracle's `StateChange` to fire |
| **Redis** (RedLock family) | Lua compare-and-`DEL` on dispose (`if get(key)==lockId then del(key)`); **otherwise the key's PX expiry** | **YES — `Expiry`, default 30s.** Configurable, min 0.1s, may not be infinite | **Yes.** `ExtensionCadence`, default **1/3 of `MinValidityTime`** = **9s** (`MinValidityTime` defaults to 90% of `Expiry` = 27s). Cannot be disabled | **YES — by design.** A 30s+ hang, GC pause or partition and the key expires; another process takes it lawfully | **Supported**, but it is a *renewal-failure* detector, not a real-time one. Fires when a `pexpire` extend fails on a majority, or when 30s elapse with no successful extend — measured on a **local `Stopwatch` that the same pause freezes** |
| **Azure** (blob leases) | Blob **deleted** on dispose if the library created the blob (the default); else `ReleaseAsync` on the lease; **otherwise the lease duration expires** | **YES — `Duration`, default 30s.** Azure constrains it to **15–60s or infinite (`-1`)** | **Yes.** `RenewalCadence`, default **Duration/3 = 10s**. Set to `Timeout.InfiniteTimeSpan` to disable | **YES — by design**, same shape as Redis. **Except with `Duration(-1)`, where it inverts**: the lease never expires, so a dead process holds it *forever* | **Supported.** Renew success/failure drives it. **With an infinite duration and no renewal the monitor loop never runs and the token never fires** |
| **MongoDB** | `DeleteOne` filtered on `{_id, lockId}` on dispose; **otherwise the `expiresAt` field lapses** (a TTL index only *tidies up*, it is not what makes the lock available) | **YES — `Expiry`, default 30s.** Configurable, min 0.1s, may not be infinite | **Yes.** `ExtensionCadence`, default **Expiry/3 = 10s** | **YES — by design.** Note the clock is the **server's** (`$$NOW` in the update pipeline), which removes client clock skew but not the pause problem | **Supported.** An extend whose `MatchedCount == 0` means someone else owns the document → `Lost` |
| **ZooKeeper** | **Ephemeral znode deleted** — explicitly on dispose, or by the ensemble when the **session** expires | **In effect yes: `SessionTimeout`, default 20s** — negotiated, bounded to 2×–20× `tickTime` (4–40s at default `tickTime=2000`). **But no wall clock**: it's missed heartbeats counted by the quorum | Renewal is the ZK client's own protocol PING, not the library's. No DistributedLock-level cadence option | **YES** — paused past the session timeout, the ensemble expires the session and deletes the znode, and the client isn't told until it reconnects | **Best in class.** Two sources ORed: the session-lost token **and** a live ZK **watch** on the node's existence — event-driven, not polled |
| **FileSystem** | **OS closes the file handle** — on `Dispose`, on process exit, or on `SIGKILL`. `DeleteOnClose` removes the file too, but **on Unix that delete is managed code, so `SIGKILL` leaves the file behind** (lock still released) | **None** | No | **No, on a local filesystem** — a paused process still owns the handle; the kernel is the arbiter. **On NFS/SMB, do not rely on it**: advisory `flock`, silently degraded, failures swallowed | **NOT SUPPORTED.** `HandleLostToken` returns `CancellationToken.None`; `CanBeCanceled == false` |
| **WaitHandles** (Windows only) | `EventWaitHandle.Set()` on dispose. On process death the kernel object is destroyed **only when the last handle closes**; waiters recover by periodically re-creating it (`abandonmentCheckCadence`, default **2s**) | **None** | No | **No** — but the *recovery* path is the weak spot, not the holding path (see caveats) | **NOT SUPPORTED.** Returns `CancellationToken.None` |

**Legend for "Can go stale mid-work?" — three distinct mechanisms, not two:**

- **Yes, by wall clock** — a timestamp deadline you don't control lapses while you are paused: **Redis, Azure,
  MongoDB**. Unfixable by configuration; only a fencing token helps.
- **Yes, by missed heartbeat** — a quorum stops hearing from you and revokes your session: **ZooKeeper**. Same
  outcome, but no clocks are compared, so clock skew and NTP steps are not in the threat model.
- **Yes, by session death** — something external deletes your session; no clock is involved at all: **SqlServer,
  Postgres, MySql, Oracle**. Bounded by detection lag, which you can shrink.
- **No** — the kernel is the arbiter and a paused process still owns the resource: **FileSystem, WaitHandles**
  (single-machine only, and read the FileSystem network-filesystem caveats before assuming otherwise).

---

## Per-provider detail

### 1. `DistributedLock.SqlServer` — `sp_getapplock`

**Release mechanism.** On dispose the library calls `dbo.sp_releaseapplock` with the same `@Resource` and
`@LockOwner`. If the process dies without disposing, the lock is released when **the SQL Server session (SPID) ends**
— which happens when the TCP connection drops, when the server kills the session, or when the client process exits
and the OS closes the socket.

**`@LockOwner` — the mode question.** Note that `sp_getapplock`'s *own* default is `Transaction`; the library
overrides it from a single line
([`SqlApplicationLock.cs`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.SqlServer/SqlApplicationLock.cs)):

```csharp
command.AddParameter("LockOwner", connection.HasTransaction ? "Transaction" : "Session");
```

So **DistributedLock's default is `Session`**, inverting the stored procedure's. You get `Transaction` only if you
opted into `UseTransaction(true)` in `SqlConnectionOptionsBuilder`, or constructed the lock from an `IDbTransaction`.
`UseTransaction` defaults to **`false`** and is **mutually exclusive with `UseMultiplexing`** (which defaults to
`true`) — the builder throws `ArgumentException` if you set both.

**TTL.** None. Microsoft's Remarks enumerate the release events exhaustively, and no clock appears among them:
*"Locks placed on a resource are associated with either the current transaction or the current session. Locks
associated with the current transaction are released when the transaction commits or rolls back. Locks associated
with the session are released when the session is logged out. When the server shuts down for any reason, all locks
are released."* `@LockTimeout` is an **acquire** timeout, not a hold expiry. Locks are also **reference-counted**:
*"When an application calls `sp_getapplock` multiple times for the same lock resource, `sp_releaseapplock` must be
called the same number of times to release the lock."*

**Auto-renewal.** None, because there is nothing to renew. What *does* run in the background is a **keepalive**:
`KeepaliveCadence` defaults to **10 minutes**
([`SqlConnectionOptionsBuilder.GetOptions`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.SqlServer/SqlConnectionOptionsBuilder.cs)),
issuing `SELECT 0 /* DistributedLock connection keepalive */` to stop Azure SQL's connection governor from reaping an
idle lock-holding connection. Note this is the *only* provider whose keepalive is on by default; it exists purely
because of Azure SQL.

**Can it go stale?** **Yes — but only through session death, never through a clock.** The concrete one that is
actually documented is **Azure SQL's 30-minute gateway idle timeout**: *"Idle by the Azure SQL Gateway, where TCP
keepalive messages might be occurring (which makes the connection not idle from a TCP perspective), but not had an
active query in 30 minutes. In this scenario, the Gateway will determine that the TDS connection is idle at 30
minutes and terminates the connection."* That is exactly what a lock-holding connection looks like, and it is
precisely why this is the only provider in the library shipping keepalive **on** by default at 10 minutes. Beyond
that: an admin `KILL`, an AG failover, or a TCP keepalive timeout.

**Loss detection.** Supported and *active*, but only for internally-owned connections. When you first read
`HandleLostToken`, the handle asks `ConnectionMonitor` for a monitoring handle, which parks a long-running sleep query
on the connection —
[`SqlDatabaseConnection.SleepAsync`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.SqlServer/SqlDatabaseConnection.cs)
issues `WAITFOR DELAY` in 1-minute increments. A server-side kill makes that query fail, `DbConnection.StateChange`
fires, and the token is cancelled. **For an externally-owned `IDbConnection`/`IDbTransaction` no background query is
ever issued** — `ConnectionMonitor.StartMonitorWorkerIfNeededNoLock` returns immediately for
`_isExternallyOwnedConnection` — because running queries on someone else's connection would violate its
thread-safety. Detection there is passive: it depends on the ADO.NET object noticing on its own.

**Pooling caveat.** Because the library holds a dedicated open connection for the lock's lifetime, ADO.NET pooling
does not interfere with its *own* locks. It matters for externally-owned connections, though, and the mechanics are
worth knowing: `Close()`/`Dispose()` on a pooled `SqlConnection` **does not end the SPID** — *"the pooler returns it
to the pooled set of active connections instead of closing it"* — and an idle pooled connection is only reaped after
"approximately 4-8 minutes". So a leaked Session-owned applock can outlive the code that took it and ride a recycled
connection into unrelated work. Whether `sp_reset_connection` clears it is **not documented** — see Unverified/open.

**Deadlock caveat.** `sp_getapplock` participates in real deadlock detection and returns `-3`, but *"a deadlock with
an application lock doesn't roll back the transaction that requested the application lock. Any rollback that might be
required as a result of the return value must be done manually."* DistributedLock surfaces this as `DeadlockException`.

### 2. `DistributedLock.Postgres` — advisory locks

**Release mechanism.** On dispose, `SELECT pg_advisory_unlock(...)` (or `pg_advisory_unlock_shared`). If the process
dies, the lock goes when the **backend session** ends.

**Session vs transaction scope.** The command text is assembled in
[`PostgresAdvisoryLock.CreateAcquireCommand`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Postgres/PostgresAdvisoryLock.cs):
it appends `_xact` if and only if `connection.HasTransaction`, producing `pg_advisory_xact_lock` /
`pg_try_advisory_xact_lock`. `UseTransaction` in `PostgresConnectionOptionsBuilder` defaults to **`false`**, so the
**default is session-scoped**. There is additionally a separate static API family —
`PostgresDistributedLock.AcquireWithTransaction` / `TryAcquireWithTransactionAsync` —  which is *acquire-only and
returns no handle*, precisely because "Postgres offers no way to explicitly release transaction-scoped locks and the
caller controls the transaction." The release is your `COMMIT`/`ROLLBACK`.

A nice implementation detail worth knowing: the acquire command wraps itself in a `SAVEPOINT` so its
`SET LOCAL statement_timeout` / `SET LOCAL lock_timeout` don't leak into your transaction, and it re-checks
`pg_locks` after a `55P03` (`lock_timeout`) because Postgres has a race where the lock can be granted at the instant
you time out ([issue #147](https://github.com/madelson/DistributedLock/issues/147)).

**TTL.** None. PostgreSQL states the lifetime rule directly: *"Once acquired at session level, an advisory lock is
held until explicitly released or the session ends."* There is no expiry, and — unlike MySQL and Oracle — Postgres
does not even offer an *acquire* timeout: your only choices are the blocking `pg_advisory_lock` or the immediate
`pg_try_advisory_lock`. (DistributedLock synthesises a timeout by setting `SET LOCAL lock_timeout`.)

The cleanup guarantee on an ungraceful death is unusually explicit, in the `pg_advisory_unlock_all` docs:
*"(This function is implicitly invoked at session end, even if the client disconnects ungracefully.)"*

**Auto-renewal.** None. `KeepaliveCadence` defaults to **OFF** (`Timeout.InfiniteTimeSpan`).

**Can it go stale?** **Yes — through session death only.** Three distinct paths, in ascending order of how likely
they are to actually get you:

1. **Server-side idle timeouts.** `idle_session_timeout` and `idle_in_transaction_session_timeout` will terminate a
   session that is merely waiting for a query — exactly what a lock-holding connection looks like. **Both default to
   `0`, i.e. disabled**, so this is only a hazard where someone has turned them on (which plenty of shops do). Note
   PostgreSQL's own warning attached to `idle_session_timeout`: *"Be wary of enforcing this timeout on connections
   made through connection-pooling software or other middleware, as such a layer may not react well to unexpected
   connection closure."*
2. **PgBouncer in transaction pooling mode silently destroys this lock.** This is the one that bites in production,
   and it is authoritatively documented: PgBouncer's SQL feature matrix lists **"Session-level advisory locks —
   Session pooling: Yes / Transaction pooling: Never."** A session-level advisory lock is bound to a backend;
   transaction pooling *"assigns a server connection to a client only during a transaction"* and returns it to the
   pool at commit. Your `pg_advisory_lock` and your `pg_advisory_unlock` can land on different backends, and your
   "held" lock can be sitting on a backend now serving someone else. `pg_advisory_xact_lock` is fine under
   transaction pooling — which is a strong argument for `UseTransaction(true)` or the `AcquireWithTransaction` APIs
   if PgBouncer is anywhere in your path.
3. **Npgsql's own connection reset releases advisory locks.** Reading
   [`NpgsqlConnector.GenerateResetMessage`](https://github.com/npgsql/npgsql/blob/main/src/Npgsql/Internal/NpgsqlConnector.cs),
   when a pooled connection is closed Npgsql sends either `DISCARD ALL` or a hand-built equivalent that explicitly
   includes `SELECT pg_advisory_unlock_all();`. DistributedLock avoids this by keeping its connection open for the
   lock's lifetime — but it is the mechanism that will bite you if you pass in an externally-owned connection that
   something else might return to a pool.

**Loss detection.** Supported and active (`pg_sleep` as the parked query), with the same externally-owned-connection
exception as SQL Server.

**Two more Postgres-specific caveats worth knowing** (neither is a DistributedLock issue — they bite anyone hand-rolling
advisory locks alongside it). First, advisory locks **stack**: *"A lock can be acquired multiple times by its owning
process; for each completed lock request there must be a corresponding unlock request before the lock is actually
released."* Second, the docs' own dangling-lock warning about evaluation order:

```sql
SELECT pg_advisory_lock(id) FROM foo WHERE id = 12345;            -- ok
SELECT pg_advisory_lock(id) FROM foo WHERE id > 12345 LIMIT 100;  -- danger!
```

*"the second form is dangerous because the `LIMIT` is not guaranteed to be applied before the locking function is
executed. This might cause some locks to be acquired that the application was not expecting, and hence would fail to
release (until it ends the session)."* Advisory locks also share a fixed shared-memory pool sized by
`max_locks_per_transaction` × `max_connections`, capping the total grantable "typically in the tens to hundreds of
thousands."

### 3. `DistributedLock.MySql` — `GET_LOCK`

**Release mechanism.** `DO RELEASE_LOCK(@name)` on dispose; **session end** otherwise.

**TTL.** None — and this is **the single most commonly misread parameter of the four relational primitives.**
`GET_LOCK(name, timeout)`'s second argument is an **acquire** timeout: *"Tries to obtain a lock with a name given by
the string `str`, using a timeout of `timeout` seconds… Returns `1` if the lock was obtained successfully, `0` if the
attempt timed out."* `GET_LOCK('x', 10)` does **not** mean "hold for 10 seconds"; it means "wait up to 10 seconds to
get it." The library passes your acquire timeout there
(`timeout.IsInfinite ? 0xFFFFFFFF : timeout.InSeconds`, because `-1` works on MySQL but not MariaDB).

MySQL states the release set exhaustively, and note the transaction clause: *"A lock obtained with `GET_LOCK()` is
released explicitly by executing `RELEASE_LOCK()` or implicitly when your session terminates (either normally or
abnormally). **Locks obtained with `GET_LOCK()` are not released when transactions commit or roll back.**"* That last
sentence is why DistributedLock's `IDbTransaction` constructor for MySQL is documented as "generally equivalent to
using an `IDbConnection` (the lock is still connection-scoped)" — MySQL simply has no transaction-scoped mode.

**Auto-renewal.** None. But **keepalive defaults to 3.5 hours** —
`options?._keepaliveCadence ?? TimeSpan.FromHours(3.5)` in
[`MySqlConnectionOptionsBuilder.GetOptions`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.MySql/MySqlConnectionOptionsBuilder.cs)
— chosen to sit under MySQL's 8-hour `wait_timeout`. **The published doc page contradicts the source here** (see
Unverified / open).

**Can it go stale?** **Yes — session death only.** The documented one is `wait_timeout`: *"By default, the server
closes the connection after eight hours if nothing has happened."* (MariaDB's variable page gives the literal
`28800` seconds.) Plus `KILL` and failover. The library's 3.5-hour keepalive is sized to sit comfortably inside that
8-hour window.

**Loss detection.** Supported, active, `SELECT SLEEP(...)` as the parked query.

**Provider-specific caveats.**
- **Name case-sensitivity.** MySQL's lock names are case-insensitive; DistributedLock's are case-sensitive, so names
  with uppercase characters are **hashed/transformed** under the hood. If you need to interoperate with code calling
  `GET_LOCK` directly, use lowercase and pass `exactName: true`.
- **Multiplexing and MySQL < 5.7 — the sharpest edge in this provider.** MySQL's own 5.7 doc page says it plainly:
  ***"Before MySQL 5.7, only a single simultaneous lock can be acquired and `GET_LOCK()` releases any existing
  lock."*** Taking a second named lock **silently released the first, with no error**. Multiplexing (on by default)
  deliberately puts several locks on one connection, so it **must be disabled** on pre-5.7 servers
  ([issue #123](https://github.com/madelson/DistributedLock/issues/123)). 5.7 reimplemented `GET_LOCK` on the metadata
  locking subsystem, which is what made multiple simultaneous locks possible.

### 4. `DistributedLock.Oracle` — `DBMS_LOCK`

**Release mechanism.** `SYS.DBMS_LOCK.RELEASE(lockHandle)` on dispose, where the handle comes from
`SYS.DBMS_LOCK.ALLOCATE_UNIQUE(:lockName, lockHandle)`; **session end** otherwise.

**Scope — Oracle's default is the opposite of SQL Server's.** `DBMS_LOCK.REQUEST`'s `release_on_commit` parameter
**defaults to `FALSE`**: *"Set this parameter to `TRUE` to release the lock on commit or roll-back. Otherwise, the
lock is held until it is explicitly released or until the end of the session."* DistributedLock does not pass the
parameter, so the Oracle default applies and the lock is **session-scoped**. There is no `UseTransaction` option for
Oracle at all.

**TTL — and the `expiration_secs` trap.** A held `DBMS_LOCK` has **no expiry**: *"User locks are automatically
released when a session terminates."* The parameter people mistake for a TTL is `ALLOCATE_UNIQUE`'s
**`expiration_secs`, which defaults to 864000 seconds (10 days)** and governs *"Number of seconds to wait after the
last `ALLOCATE_UNIQUE` has been performed on a specified lock, before permitting that lock to be deleted from the
`DBMS_LOCK_ALLOCATED` table."* That is **garbage collection of a dictionary row mapping your string name to a numeric
lock id** — it has zero effect on whether a held lock is still held, and a lock is not released when it elapses. The
library calls `ALLOCATE_UNIQUE` with only the name and out-parameter, so the 10-day default applies.

**Auto-renewal.** None. `KeepaliveCadence` defaults to **OFF** — the builder's own comment says "Oracle does not kill
idle connections by default", and that matches Oracle: the `DEFAULT` profile *"initially defines unlimited
resources"*, so `IDLE_TIME` is effectively `UNLIMITED` out of the box (though `RESOURCE_LIMIT` itself defaults to
`true`, so a profile that *does* set `IDLE_TIME` will be enforced).

**Can it go stale?** **Yes — session death only** (a profile `IDLE_TIME` limit if someone set one,
`ALTER SYSTEM KILL SESSION`, failover).

**Oracle's genuine advantage.** Its user locks live in the real lock manager, so they get **deadlock detection** for
free — the only one of the four with an unqualified yes there. `REQUEST` also returns `4` ("Already own lock") rather
than silently re-granting, unlike Postgres's stacking. Oracle does caution that `DBMS_LOCK` *"is most efficient with a
limit of a few hundred locks for each session."*

**Loss detection.** Supported, active, `BEGIN sys.DBMS_SESSION.SLEEP(:seconds); END;` as the parked query — plus a
genuinely interesting workaround: Oracle's driver doesn't raise `StateChange` unless the state is observed, so
[`OracleDatabaseConnection.SleepAsync`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Oracle/OracleDatabaseConnection.cs)
does `_ = this._innerConnection.State;` in its catch block specifically to make the event fire
([oracle/dotnet-db-samples#226](https://github.com/oracle/dotnet-db-samples/issues/226)).

**Setup caveat.** You may need `grant execute on SYS.DBMS_LOCK to someuser;` or you'll get
`identifier 'SYS.DBMS_LOCK' must be declared ORA-06550`. Also: the Oracle .NET client has no true async I/O, so the
synchronous APIs are marginally faster here.

### 5. `DistributedLock.Redis` — RedLock family

**Algorithm.** Textbook single-instance Redis locking, generalised to N instances.
[`RedisMutexPrimitive`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Redis/Primitives/RedisMutexPrimitive.cs):

- **Acquire:** `StringSet(key, lockId, expiry, When.NotExists, CommandFlags.DemandMaster)` — i.e. `SET key val NX PX`.
- **Release:** Lua, `if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) end return 0`.
- **Extend:** Lua, same guard then `pexpire`.
- `lockId` is `{MachineName}_{ProcessId}_{Guid:n}`.
- `CommandFlags.DemandMaster` on every operation — never a replica.

**Multi-node?** **Yes.** `RedisDistributedLock(RedisKey key, IEnumerable<IDatabase> databases, ...)` runs true
RedLock: acquire in parallel, succeed on a **majority** (`(count / 2) + 1`, in `RedLockHelper.HasSufficientSuccesses`),
release everything otherwise. `RedisDistributedReaderWriterLock` too. **`RedisDistributedSemaphore` does not** — and
the docs give the correct reason: RedLock's majority argument does not preserve a semaphore's invariant (three
databases, two-count semaphore, three users, everyone wins two out of three). With a multi-database provider,
`CreateSemaphore()` silently uses **the first database in the list**.

**Defaults, from
[`RedisDistributedSynchronizationOptionsBuilder`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Redis/RedisDistributedSynchronizationOptionsBuilder.cs):**

| Option | Default | Notes |
| --- | --- | --- |
| `Expiry` | **30s** | Min 0.1s; **may not be infinite** |
| `MinValidityTime` | **90% of `Expiry` = 27s** | How much validity must remain for the acquire to count |
| `ExtensionCadence` | **`MinValidityTime` / 3 = 9s** | **Cannot be disabled** — see below |
| `BusyWaitSleepTime` | **[10ms, 800ms]** | Randomised per sleep |
| *(derived)* `AcquireTimeout` | **`Expiry - MinValidityTime` = 3s** | Each individual acquire/extend attempt gets only this long |

That last derived value is easy to miss and matters: `RedLockTimeouts.AcquireTimeout => Expiry - MinValidityTime`, so
with defaults **each acquire attempt and each extend attempt has a 3-second budget**.

**Auto-extension cannot be turned off**, deliberately. The builder rejects an `ExtensionCadence >= MinValidityTime`,
and the source explains why: *"we do not allow for disabling auto-extension here because it leads to traps where
people might abandon the handle and then have it be closed due to GC"*
([issue #130](https://github.com/madelson/DistributedLock/issues/130)).

**Can it go stale?** **Yes, and this is the headline case.** A GC pause, a descheduled container, or a partition
longer than 30 seconds and the key expires; another process acquires it entirely lawfully. This is exactly
Kleppmann's scenario and the library's own "Other topics" page concedes it: *"Timeout-based locking approaches such
as Redis locks and Azure leases have an inherent risk that an extended hang on the machine holding the lock could
cause the timeout to expire before the lock can be automatically-renewed."*

**Redis's own documentation agrees, and goes further.** The "Disclaimer about consistency" section of redis.io's
distributed-locks page is remarkably direct, and is the best single citation for this whole document:

> "1. **You should implement fencing tokens.** This is especially important for processes that can take significant
> time and applies to any distributed locking system. Extending locks' lifetime is also an option, but don't assume
> that a lock is retained as long as the process that had acquired it is alive.
> 2. **Redis is not using monotonic clock for TTL expiration mechanism.** That means that a wall-clock shift may
> result in a lock being acquired by more than one process."

**DistributedLock's Redis provider does not expose a fencing token.** So on the library's own terms and Redis's own
terms, this provider does not meet the bar its upstream documentation sets for correctness-critical use.

Two further hazards documented by redis.io that the library cannot fix for you:
- **Replica failover is unsafe by construction.** *"By doing so we can't implement our safety property of mutual
  exclusion, because Redis replication is asynchronous."* Client A acquires on the master, the master crashes before
  the write reaches the replica, the replica is promoted, client B acquires the same lock. `CommandFlags.DemandMaster`
  ensures the library always talks to a master, but it cannot stop a failover from losing the key.
- **Instance restart breaks the majority argument.** *"A client acquires the lock in 3 of 5 instances. One of the
  instances where the client was able to acquire the lock is restarted, at this point there are again 3 instances
  that we can lock for the same resource, and another client can lock it again."* The documented mitigations are
  `fsync=always` or delayed restart — both operational, neither something the client library can arrange.

**Loss detection — and its limit.** Redis handles use the shared
[`LeaseMonitor`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/LeaseMonitor.cs).
Two things cancel `HandleLostToken`: an extend that **conclusively fails** on a majority (`LeaseState.Lost`), or a
**local `Stopwatch`** showing that `LeaseDuration` (30s) has elapsed since the last successful renew. Read that
second clause carefully — *the detector's clock is a `Stopwatch` inside the same process that is hung*. If the
process is frozen, the monitor task is frozen too, so the token fires **when the process wakes up**, which is after
the damage. `HandleLostToken` on Redis is an after-the-fact notification, not a guard. The monitor also holds only a
`WeakReference` to itself, so a GC'd handle stops renewing and lets the key expire — that is how abandonment recovery
works.

**Other notes.** Acquisition cannot truly block, so waiting is a randomised busy-wait — these locks are most
efficient with `TryAcquire` and a zero timeout. `IDatabase.WithKeyPrefix(...)` is honoured, so two same-named locks
under different prefixes do not see each other. The `RedLockAcquire` code has a subtle optimisation for the
"deciding vote" server being disconnected, bypassing StackExchange.Redis 2.5.27+'s command backlog to fail fast.

### 6. `DistributedLock.Azure` — blob leases

**The sentinel blob — confirmed.** With the common `AzureBlobLeaseDistributedLock(BlobContainerClient, name)`
constructor, the library derives a blob name from your lock name and, if that blob doesn't exist,
**creates an empty one tagged with metadata key `__DistributedLock`**. Reading
[`AzureBlobLeaseDistributedLock.cs`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Azure/AzureBlobLeaseDistributedLock.cs):

```csharp
private static readonly string CreatedMetadataKey = $"__DistributedLock";
...
var shouldDeleteBlob = isRetryAfterCreate
    || (await this._blobClient.GetMetadataAsync(leaseClient.LeaseId, ...)).ContainsKey(CreatedMetadataKey);
```

and on release:

```csharp
if (this._ownsBlob) { await this._lock._blobClient.DeleteIfExistsAsync(leaseId: ...); }
else                { await this._leaseClient.ReleaseAsync(); }
```

**What that means for fencing.** Azure's genuinely useful property is confirmed by the REST docs: *"To write to a
blob with an active lease, a client must include the active lease ID with the write request… If the lease ID isn't
included, these operations fail on a leased blob, with `412 – Precondition failed`"* (covering `Put Blob`,
`Set Blob Metadata`, `Set Blob Properties`, `Delete Blob`, `Put Block`, `Put Block List`, `Put Page`, `Append Block`).
**But that fences exactly one blob's write path and nothing else.** In the default configuration that blob is a
**sentinel the library invented**, not your protected resource — so the lease fences *nothing you care about*.

To get real fencing you must use the `AzureBlobLeaseDistributedLock(BlobBaseClient, ...)` constructor pointed at
**the actual blob you are protecting**, then pass `AzureBlobLeaseDistributedLockHandle.LeaseId` (publicly exposed)
into your conditional writes. Doing so also flips release from "delete the blob" to "release the lease", which is
what you want for a real data blob.

Three scope limits to keep in mind even then: **reads are not fenced** (*"It's not necessary to include the lease ID
for `GET` operations"* — enforcing read exclusivity is left to you); **the container is not protected**
(*"a container can be deleted even if blobs within it have active leases"*); and this only works when the protected
resource *is* an Azure blob. Protecting a database with an Azure lease gets you the sentinel case whether you meant
it or not.

**TTL and renewal, from
[`AzureBlobLeaseOptionsBuilder`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Azure/AzureBlobLeaseOptionsBuilder.cs):**

| Option | Default | Constraint |
| --- | --- | --- |
| `Duration` | **30s** | Azure permits **15s–60s, or infinite (`-1`)**; the builder enforces exactly that |
| `RenewalCadence` | **`Duration` / 3 = 10s** | `Timeout.InfiniteTimeSpan` disables renewal |
| `BusyWaitSleepTime` | **[250ms, 1s]** | |

Azure's own numbers match the builder's validation exactly: *"The lock duration can be 15 to 60 seconds, or can be
infinite"*, and `x-ms-lease-duration` *"can't be changed by using `renew` or `change`."*

**Can it go stale?** **Yes**, identically to Redis: pause longer than 30s and the lease lapses.

**Two Azure semantics that materially soften this, and that the library exploits.** First, **an expired lease is
still renewable**: *"the lease can be renewed even if it has expired, as long as the blob hasn't been modified or
leased again since the expiration of that lease."* Microsoft spells out the implication — *"A client can attempt to
renew or release the lease by using their expired lease ID. If the operation is successful, this means that the blob
hasn't been changed since the lease ID was last valid."* So a late renewal that **succeeds** is positive evidence
nobody took over, and one that **fails with 409** is positive evidence somebody did. That is a genuinely better
signal than Redis's, and it is exactly what `RenewOrValidateLeaseAsync` maps onto `Renewed` / `Lost`. Distinguish
**Expired** (unlocked but renewable) from **Broken** (*"After a lease is broken, it can't be renewed"*).

Second, why the library **deletes** the sentinel blob rather than releasing the lease: *"If a lease expires rather
than being explicitly released, a client might need to wait up to one minute before a new lease can be acquired for
the blob."* Deleting the blob sidesteps that minute entirely, which matters a lot for a lock whose whole point is to
be handed over quickly.

**The infinite-duration inversion — a real trap.** `Duration(Timeout.InfiniteTimeSpan)` is accepted (`TimeoutValue`
compares infinite as greatest, so both range checks pass). It then defaults `RenewalCadence` to infinite as well, and
`MonitoringCadence` becomes `Duration` — also infinite. The `LeaseMonitor` loop's first statement is
`await Task.Delay(monitoringCadence.InMilliseconds, ...)` with `-1`, so **the loop body never executes**. Net effect:
no renewal, no validation, `HandleLostToken` never fires, and — because the lease never expires — **a process that
dies holds the lock forever** until someone issues a Break Lease. This is the one configuration in the whole library
that defeats the documented "handle abandonment will not cause a lock to be held forever" guarantee. The handle's own
source comment acknowledges the shape of it: managed finalization exists partly for *"helping release
infinite-duration leases (rare case)."*

**Loss detection.** Supported. When renewal is on, a successful `RenewAsync` → `Renewed`, a failure → `Lost`. When
renewal is off but the duration is finite, the monitor instead does a metadata `GET` **using the lease ID** purely to
check whether someone else has taken over → `Held` or `Lost`.

### 7. `DistributedLock.MongoDB`

**How it actually works** — not a TTL collection in the sense people usually mean. It is a **lease document** with an
`expiresAt` field, acquired by one atomic `findOneAndUpdate` with an **aggregation-pipeline update**:

```
_id          = lock key
lockId       = per-acquisition GUID  (the Redis "random value" equivalent)
acquiredAt   = $$NOW
expiresAt    = $dateAdd($$NOW, Expiry)
fencingToken = monotonically incremented, but only when actually acquiring
```

The pipeline computes `expiredOrMissing = ifNull(expiresAt, epoch) <= $$NOW` and uses `$cond` to overwrite each field
*only if* that's true, with `IsUpsert = true` and `ReturnDocument.After`. The caller then verifies
`result?.LockId == lockId` — the same compare-your-own-token pattern as Redis. The atomicity this leans on is
documented: *"In MongoDB, write operations are atomic on the single-document level, even if modifying multiple
values"*, and *"When modifying a single document, both `findAndModify` and the `updateOne()` method atomically update
the document."*

**Whose clock is `$$NOW`?** MongoDB documents it as *"A variable that returns the current datetime value. `NOW`
returns the same value for all members of the deployment and remains the same throughout all stages of the
aggregation pipeline."* The docs never literally say "the server's clock" — but since it is a server-evaluated
aggregation variable that the client never supplies, and since it is identical across deployment members, **the
timestamp must originate server-side**. (Flagging that as inference, not quotation.) The practical consequence is
real and good: N contending clients with N skewed clocks all agree on expiry, which is precisely the property Redlock
lacks. It does nothing about the process-pause problem.

**Release.** `DeleteOne` filtered on `{_id: key, lockId: myLockId}` — it will not delete someone else's lock.

**The TTL index is housekeeping, not correctness — and MongoDB's docs explain why that distinction is load-bearing.**
`MongoIndexInitializer` fire-and-forget creates an `expiresAt_ttl` index with `ExpireAfter = TimeSpan.Zero` on first
successful acquire, and its own doc comment says *"Note: TTL monitors run on a schedule; correctness MUST NOT depend
on this index existing."* That is exactly right, because MongoDB's TTL monitor is slow and its lag is **unbounded
above**: *"The background task that removes expired documents runs every 60 seconds"*, *"The TTL index does not
guarantee that expired data is deleted immediately upon expiration"*, and — the important one —
*"**Because the duration of the removal operation depends on the workload of your `mongod` instance, expired data may
exist for some time beyond the 60 second period between runs of the background task.**"* On a replica set the TTL
thread runs only on the primary and is idle on secondaries.

**Had the provider relied on the index for availability, an abandoned lock could stay unavailable for minutes.** It
doesn't: what makes an abandoned lock re-acquirable is the `expiresAt <= $$NOW` comparison *inside the acquire
pipeline*, which is evaluated at acquire time. The index only stops dead documents accumulating, and the initializer
degrades gracefully if it lacks index-creation permission. This is the right design, and it is worth understanding
because plenty of hand-rolled Mongo locks get it wrong in exactly this way.

**Defaults:** `Expiry` **30s** (min 0.1s, non-infinite), `ExtensionCadence` **`Expiry`/3 = 10s`**, `BusyWaitSleepTime`
**[10ms, 800ms]**, collection name **`distributed_locks`** (note: the const in source is `distributed_locks`, while
the doc page prose says `"distributed.locks"` — see Unverified / open).

**Can it go stale?** **Yes**, same shape as Redis and Azure.

**The differentiator — a real fencing token.** `MongoDistributedLockHandle` exposes
`public long FencingToken { get; }`. A repo-wide grep confirms **no other provider in the library exposes anything
comparable.** If you are going to use a TTL-based lock and you care about correctness, this is the only provider that
hands you the tool to make the protected resource reject a stale writer.

**Loss detection.** Supported via `LeaseMonitor`; the renew is an `UpdateOne` on `{_id, lockId}` and
`MatchedCount == 0` means someone else owns the document → `Lost`.

### 8. `DistributedLock.ZooKeeper`

**Release mechanism.** The lock is an **`EPHEMERAL_SEQUENTIAL` znode**
([`ZooKeeperNodeCreator`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.ZooKeeper/ZooKeeperNodeCreator.cs)).
Dispose deletes it explicitly (and tries to delete the parent directory, tolerating `NotEmptyException`). If the
process dies, **the ensemble deletes the znode when the session expires**. This is the standard ZK lock recipe:
create an ephemeral sequential child, watch the next-lowest sibling.

**Is the session timeout a TTL by another name? Yes as to *effect*, no as to *mechanism* — and the distinction is
the most interesting thing about this provider.** `SessionTimeout` defaults to **20s**
([`ZooKeeperDistributedSynchronizationOptionsBuilder`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.ZooKeeper/ZooKeeperDistributedSynchronizationOptionsBuilder.cs)),
and the source comment's reasoning checks out against ZooKeeper's docs exactly: *"The client sends a requested
timeout, the server responds with the timeout that it can give the client. The current implementation requires that
the timeout be a minimum of 2 times the `tickTime` … and a maximum of 20 times the `tickTime`."* With the shipped
default `tickTime=2000`, the negotiable band is **4s–40s**, so 20s sits comfortably inside it. `ConnectTimeout`
defaults to **15s**. The library's doc states the trade-off plainly: *"Lower values mean that locks will be released
more quickly following a crash of the lock-holding process, but also increase the risk that transient connection
issues will result in a dropped lock."*

**As to effect**, that is a TTL: exceed it and you lose the lock. **As to mechanism, it is meaningfully better than
Redis or Azure on two counts.**

1. **No wall clock is involved.** ZooKeeper is explicit: *"Real time — ZooKeeper doesn't use real time, or clock
   time, at all except to put timestamps into the stat structure on znode creation and znode modification."* The
   session dies because the ensemble stopped hearing heartbeats, not because two machines compared their clocks.
   That removes the entire class of failure redis.io warns about ("a wall-clock shift may result in a lock being
   acquired by more than one process").
2. **The quorum is the authority, and it acts.** *"Session expiration is managed by the ZooKeeper cluster itself, not
   by the client… At session expiration the cluster will delete any/all ephemeral nodes owned by that session and
   immediately notify any/all connected clients of the change."* Compare Redis, where expiry is a passive key TTL
   and nobody is notified of anything.

**Auto-renewal.** No DistributedLock-level option — it's the ZK client's own protocol ping: *"The session is kept
alive by requests sent by the client. If the session is idle for a period of time that would timeout the session, the
client will send a PING request to keep the session alive."* There is no `RenewalCadence` here.

**Can it go stale? Yes — and ZooKeeper's own docs describe the window in unusual detail.** Pause past the session
timeout and the ensemble expires the session and deletes your znode. The timeline from the Programmer's Guide is worth
quoting in full, because the third line is the staleness window:

> 'connected' : session is established and client is communicating with cluster
> …. client is partitioned from the cluster
> 'disconnected' : client has lost connectivity with the cluster
> **…. time elapses, after 'timeout' period the cluster expires the session, nothing is seen by client as it is disconnected from cluster**
> …. time elapses, the client regains network level connectivity with the cluster
> 'expired' : eventually the client reconnects to the cluster, it is then notified of the expiration

Note the tension with what the **Recipes** page claims for the lock recipe — *"Fully distributed locks that are
globally synchronous, meaning at any snapshot in time no two clients think they hold the same lock."* Read alongside
the timeline above, that claim is not quite right: during the partition window the ensemble has released the lock and
the old client has not been told. The ZooKeeper docs never reconcile these two passages (see Unverified / open).

**Loss detection — the best of the ten.** `ZooKeeperNodeHandle` builds `HandleLostToken` as a linked token over
*two* independent signals:
1. The connection's `ConnectionLostToken`, which fires either immediately on a `KeeperState.Expired` event, or via
   `CancelAfter(sessionTimeout)` when the client goes into reconnecting state (because *"if the connection goes down
   and never recovers, we'll never get the session expired notification"*).
2. A live **ZooKeeper watch** on the node's own existence — `WaitForNotExistsOrChangedAsync` re-arms a watch in a loop
   and cancels the token the moment the node stops existing.

That is event-driven detection of "someone deleted my lock", which no other provider offers.

**Caveat — sessions are pooled.** `ZooKeeperConnection.DefaultPool` caches sessions keyed by connection info with a
**10-minute max age**, because "the creation and closing of sessions are costly in ZooKeeper" while sessions also leak
watches over time ([ZOOKEEPER-442](https://issues.apache.org/jira/browse/ZOOKEEPER-442)). Consequence: **several locks
in your process may share one ZK session, and if that session expires they all die together.** The handle also
registers with `ManagedFinalizerQueue` precisely because a pooled session can outlive an abandoned handle.

### 9. `DistributedLock.FileSystem`

**Release mechanism — the OS, and nothing else.** The whole lock is one line
([`FileDistributedLock.TryAcquire`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.FileSystem/FileDistributedLock.cs)):

```csharp
lockFileStream = new FileStream(this.Name, FileMode.OpenOrCreate, FileAccess.Read,
                                FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
```

`FileShare.None` is the lock. `DeleteOnClose` is the cleanup. Dispose closes the stream. Contention shows up as an
`IOException`, which the code maps to "return null, retry later"; acquisition is a randomised busy-wait between
**50ms and 1s** (not configurable — the source explains this is because a future implementation may use native
blocking APIs).

**On process death the OS releases the lock — but on Unix it does *not* delete the file.** This asymmetry is
undocumented on Microsoft Learn and only visible in the runtime source. `SafeFileHandle.Unix.cs` states it directly:

```
// On Windows, DeleteOnClose happens when all kernel handles to the file are closed.
// Unix kernels don't have this feature, and .NET deletes the file when the Handle gets disposed.
```

So on Windows the kernel performs the delete and it survives `TerminateProcess`. On **Unix the `Unlink` is performed
by managed disposal code**, so a `SIGKILL` skips it and **leaves a stale lock file on disk**. The *lock* still
self-heals — the kernel closes the fd, and `flock` locks are *"released either by an explicit `LOCK_UN` operation …
or when all such file descriptors have been closed"* — but the file itself lingers. That is cosmetically alarming and
functionally harmless: the next acquirer opens the same path with `OpenOrCreate` and takes the lock fine.

**TTL / renewal.** None of either. There is no clock anywhere in this provider.

**Can it go stale?** **On a local filesystem, no.** A paused process still owns its file handle; the kernel is the
arbiter and it does not care that you are unresponsive. Liveness is enforced by process death alone
(*"Any open file descriptors belonging to the process are closed"* on Unix; *"All kernel objects are closed"* on
Windows). This is genuinely the strongest staleness story in the library — at the cost of only working on one machine.

**Three caveats that the .NET API documentation does not tell you.** `FileShare`'s docs read like a mandatory lock —
*"Declines sharing of the current file. Any request to open the file (by this process or another process) will fail
until the file is closed"* — with no platform notes at all. The runtime source tells a different story:

1. **On Unix it is advisory, and not atomic with the open.** From `SafeFileHandle.Unix.cs`: *"Lock the file if
   requested via FileShare. **This is only advisory locking.** FileShare.None implies an exclusive lock on the file
   and all other modes use a shared lock. While this is not as granular as Windows, **not mandatory, and not atomic
   with file opening**, it's better than nothing."* Advisory means a process that doesn't ask is not stopped.
2. **Lock failures are silently swallowed.** The same code catches the `flock` error and only rethrows `EWOULDBLOCK`:
   *"Other errors, such as ENOTSUP (locking isn't supported) or EACCES (the file system doesn't allow us to lock),
   will only hamper FileStream's usage without providing value."* On a filesystem that cannot lock, **`FileShare.None`
   provides no exclusion at all and reports success.**
3. **It can be switched off by environment variable.** `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` (or the
   `System.IO.DisableFileLocking` AppContext switch) makes file locking a process-wide no-op.

**On a network filesystem: do not rely on it.** The library's own doc warns *"this should be tested because the
network file system may not truly support locking"*, and the maintainer was asked directly in
[issue #77](https://github.com/madelson/DistributedLock/issues/77): *"I haven't tried this… My understanding from
reading docs is that this may or may not work depending on the particular file share technology being employed."*
The primary sources sharpen that hedge into a warning:

- `flock(2)`: *"Up to Linux 2.6.11, `flock()` does not lock files over NFS"*; since 2.6.12 NFS clients *"support
  `flock()` locks by emulating them as `fcntl(2)` byte-range locks on the entire file"*; and since 2.6.37 the
  **`local_lock` mount option can treat `flock()` locks as local** — which would let two machines both believe they
  hold the lock, silently.
- **dotnet/runtime distrusts these filesystems enough to special-case them.** `pal_io.c` carries the comment
  *"LOCK_SH does not work well for write access on nfs/cifs/samba. For example, writes are dropped silently"* and
  refuses to lock on `nfs`/`cifs`/`smb`/`smb2`. Read the guard carefully though: the suppression applies to `LOCK_SH`
  with write access, so **`FileShare.None` (`LOCK_EX`) still attempts `flock` on NFS/SMB** — leaving you exposed to
  emulation and `local_lock` semantics, with failures swallowed per point 2 above.

**Loss detection.** **Not supported.** `FileDistributedLockHandle.HandleLostToken` returns `CancellationToken.None`,
so `CanBeCanceled` is `false`. The library's test suite confirms this by omission — `PrepareForHandleLost()` is not
overridden for the FileSystem strategy, and `DistributedLockCoreTestCases` asserts
`handle.HandleLostToken.CanBeCanceled.ShouldEqual(handleLostHelper != null)`.

**Other caveats.** Locking a **read-only** file throws `NotSupportedException` (because `DeleteOnClose` can't work).
A path that is already a directory throws `InvalidOperationException`. The code retries transient
`UnauthorizedAccessException`/`IOException` up to **1600 times** because concurrent create/delete races produce them
spuriously.

### 10. `DistributedLock.WaitHandles` (Windows only)

**Release mechanism — and the surprising choice of primitive.** This is **not** a named `Mutex`. It is a named,
**auto-reset `EventWaitHandle`** created in the *signalled* state, where "acquire" means `WaitOne()` (consuming the
signal) and "release" means `Set()`. Confirmed in
[`EventWaitHandleDistributedLockHandle.Dispose`](https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.WaitHandles/EventWaitHandleDistributedLockHandle.cs).

The reason matters, because a `Mutex` *would* give you true OS-level abandonment (`AbandonedMutexException`). The
maintainer explains the trade in [issue #77](https://github.com/madelson/DistributedLock/issues/77): *"One of the
annoying things about `Mutex` as opposed to `EventWaitHandle` is that a held mutex is associated with a specific
thread, which will make it a bit tricky to get the async semantics to work properly."* Thread affinity is
incompatible with `async`, so the library trades away free abandonment recovery and re-implements it by hand.

**The hand-rolled abandonment recovery.** Because an unsignalled event is *not* auto-released when its owner dies,
waiters periodically dispose their handle and re-create it (`abandonmentCheckCadence`, default **2s**). If the crashed
owner was the last holder of a handle to that name, the kernel object is destroyed on dispose, and the next
`new EventWaitHandle(initialState: true, ...)` **creates a fresh, signalled one** — the lock becomes available. The
library's `TestCrossProcessAbandonmentWithKill` exercises exactly this and expects recovery.

**TTL / renewal.** None of either.

**Can it go stale?** **No** in the ordinary sense — a paused holder keeps the event unsignalled and nobody else gets
in. The weakness is on the *recovery* side rather than the holding side; see Unverified / open for a concern I could
not resolve.

**Loss detection.** **Not supported.** Returns `CancellationToken.None`.

**Other caveats.** Windows-only — there is no runtime platform guard in the library, so on Unix you get whatever .NET
throws for named wait handles. Names are forced under a `Global\` prefix (max 260 chars) unless `exactName: true`.
The *semaphore* variant is the single primitive in the entire library whose test infrastructure declares
`SupportsCrossProcessSingleSemaphoreTicketAbandonment => false` — a killed process holding one ticket of a multi-count
`WaitHandleDistributedSemaphore` does **not** release it.

---

## What this means for choosing

**If you need correctness-grade mutual exclusion, the session-scoped relational providers are categorically better,
and it is not close.** SqlServer, Postgres, MySql and Oracle share one property the lease family cannot have: **there
is no clock in the system that can decide your lock has ended.** A deadline is a decision made by a third party using
a measurement of time that your paused process is not participating in. A session is a decision made by the *presence
or absence of a live connection*, and a paused process still has one. That difference is the whole ballgame.

**But be precise about what "no clock" buys you, because it is not immunity.** Session-scoped locks still have a
staleness window, and it opens whenever *something else* kills your session: the idle-connection killer, the admin
`KILL`, the failover, the pooler. The distinction is structural:

- **Detection-lag staleness** (session-scoped) is bounded by *how fast you notice a socket died*. You can drive it
  toward zero: read `HandleLostToken` to arm the parked sleep query, and the failure surfaces within seconds.
- **Deadline staleness** (TTL-based) is bounded by *a clock you don't control and can't pause*. You cannot drive it
  to zero. Shortening the expiry makes it fire *more often*, not less dangerously. There is no configuration that
  removes it.

And note the asymmetry in what each family does when *you* are the thing that's broken. If your process hangs, a
session-scoped lock **stays held** — the failure mode is a stuck system, which is loud, diagnosable, and safe. A
lease-scoped lock **is handed to someone else** — the failure mode is silent concurrent execution, which is quiet,
hard to reproduce, and unsafe. Given a choice between a deadlock and a data race, take the deadlock.

Kleppmann's point about *why* you cannot code your way out of the deadline case deserves repeating verbatim, because
the "just check the clock before writing" fix is the first thing everyone reaches for: *"You cannot fix this problem
by inserting a check on the lock expiry just before writing back to storage. Remember that GC can pause a running
thread **at any point**, including the point that is maximally inconvenient for you (between the last check and the
write operation)."* The only real fix is to make the *resource* reject the stale writer — which is what a fencing
token is for, and why MongoDB's is the one genuinely differentiated feature among the four lease providers.

So the honest ranking, for correctness:

1. **Postgres or SqlServer, on the same database as the data you are protecting, sharing one `DbConnection` with your
   transaction.** This is the only genuinely safe configuration in the library, and the library's own docs say so:
   *"when using a SQLServer or Postgres lock to protect a resource on the same database it is possible to use the
   same `DbConnection` for both the locking operation and the data modification. Combined with database transactions,
   this guarantees the integrity of the locking."* The reason it works is that the lock and the write **die
   together** — there is no window in which the lock is gone but the write still lands. Use `UseTransaction(true)`
   (SqlServer) or the `AcquireWithTransaction` static APIs (Postgres) and let `COMMIT`/`ROLLBACK` be your release.
   Prefer this whenever it is available; everything below is a compromise.
2. **Session-scoped relational locks against a different resource.** Still no clock, but now the lock and the
   protected write can diverge. Arm `HandleLostToken`, set a `KeepaliveCadence` if anything in your path reaps idle
   connections, and — for Postgres specifically — **verify you are not behind PgBouncer in transaction mode**, which
   breaks this silently rather than loudly.
3. **ZooKeeper**, if you already run it. It is a lease in disguise, but it is the best-engineered lease here: the
   heartbeat is protocol-level rather than application-level, the ensemble is the authority, and the loss detection
   is genuinely event-driven (a watch fires the instant your znode disappears) rather than a poll that your own hang
   can freeze. If you must have a lease, have this one.
4. **FileSystem**, if single-machine is genuinely enough. No clock at all, and the kernel is the arbiter — an
   excellent safety story inside its very narrow scope. Do not extend that scope to NFS/SMB on faith.
5. **MongoDB**, if you must use a TTL lock. It is the only one that hands you a **fencing token**, which is the only
   known way to make TTL-based locking actually safe: pass `FencingToken` into every write against the protected
   resource and have the resource reject anything with a token lower than the highest it has seen. Note that this
   requires the protected resource to *cooperate* — if it can't, the token is decoration.
6. **Azure blob leases**, with a large caveat: by default they lease a **sentinel blob that fences nothing**. They
   become genuinely useful when you lease *the actual blob you are protecting* and pass `LeaseId` into your
   conditional writes — at which point Azure gives you real fencing on that one blob, for free. Used that way it
   jumps up this list; used the default way it is a bare TTL.
7. **Redis**, and be honest with yourself about why. Kleppmann's efficiency/correctness split is the right frame:
   *"if the lock fails and two nodes end up doing the same piece of work, the result is a minor increase in cost"*
   versus *"a corrupted file, data loss, permanent inconsistency, the wrong dose of a drug administered to a
   patient."* For **efficiency** locking — "I would rather not run this cron job twice" — Redis is completely fine
   and probably already in your stack. For **correctness** locking, this provider exposes no fencing token, and
   **redis.io's own documentation tells you to use one**. Note also that multi-node RedLock does not address the
   failure that actually bites: it protects against Redis *node* failure, not against *holder pauses*. Kleppmann's
   summary is harsh but earned — *"it is unnecessarily heavyweight and expensive for efficiency-optimization locks,
   but it is not sufficiently safe for situations in which correctness depends on the lock."* In fairness, antirez's
   rebuttal makes a reasonable case that a *unique* token plus compare-and-set is a workable substitute for a
   monotonic one, and he is right that overrunning your lease *"is common with all the distributed locks
   implementations."* Both sides agree on the hazard; they disagree about the fix. The library gives you neither.
8. **WaitHandles**, only for Windows-only single-machine coordination where you specifically want an OS primitive.
   `FileDistributedLock` does the same job cross-platform with a cleaner abandonment story.

**The one rule that survives all of this:** if a violation of mutual exclusion is genuinely unacceptable, the lock
should not be the last line of defence. Either bind the lock to the resource (option 1), or fence the resource
(options 5/6), or make the operation idempotent so that a double-execution is harmless. The library's own
"Other topics" page reaches the same conclusion and points at Kleppmann; that is an unusually honest thing for a
locking library to tell you, and it should be taken at face value.

---

## Sources

### Library source (`madelson/DistributedLock` @ master, release 2.8.3, commit `8007a86`)

- Repo root — https://github.com/madelson/DistributedLock
- `LeaseMonitor` (shared TTL/renew/loss engine for Redis, Azure, Mongo) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/LeaseMonitor.cs
- `ConnectionMonitor` (keepalive + `HandleLostToken` for all four relational providers) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/Data/ConnectionMonitor.cs
- `DedicatedConnectionOrTransactionDbDistributedLock` — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/Data/DedicatedConnectionOrTransactionDbDistributedLock.cs
- `MultiplexedConnectionLock` — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/Data/MultiplexedConnectionLock.cs
- `ManagedFinalizerQueue` (30s cadence; abandonment recovery) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/ManagedFinalizerQueue.cs
- `TimeoutValue` (infinite compares as greatest — relevant to the Azure trap) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Core/Internal/TimeoutValue.cs
- `SqlApplicationLock` (`@LockOwner`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.SqlServer/SqlApplicationLock.cs
- `SqlConnectionOptionsBuilder` (keepalive 10m) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.SqlServer/SqlConnectionOptionsBuilder.cs
- `SqlDatabaseConnection` (`WAITFOR DELAY`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.SqlServer/SqlDatabaseConnection.cs
- `PostgresAdvisoryLock` (`_xact` selection, savepoints, `55P03` recheck) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Postgres/PostgresAdvisoryLock.cs
- `PostgresConnectionOptionsBuilder` (keepalive OFF) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Postgres/PostgresConnectionOptionsBuilder.cs
- `PostgresDistributedLock.Transactions` (acquire-only transaction APIs) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Postgres/PostgresDistributedLock.Transactions.cs
- `MySqlUserLock` — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.MySql/MySqlUserLock.cs
- `MySqlConnectionOptionsBuilder` (keepalive **3.5h**) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.MySql/MySqlConnectionOptionsBuilder.cs
- `OracleDbmsLock` — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Oracle/OracleDbmsLock.cs
- `OracleDatabaseConnection` (StateChange workaround) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Oracle/OracleDatabaseConnection.cs
- `RedisMutexPrimitive` (`SET NX PX` + Lua) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Redis/Primitives/RedisMutexPrimitive.cs
- `RedisDistributedSynchronizationOptionsBuilder` (30s / 27s / 9s) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Redis/RedisDistributedSynchronizationOptionsBuilder.cs
- `RedLockAcquire` / `RedLockExtend` / `RedLockHelper` (majority logic) — https://github.com/madelson/DistributedLock/tree/master/src/DistributedLock.Redis/RedLock
- `RedLockTimeouts` (`AcquireTimeout = Expiry − MinValidityTime`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Redis/RedLock/RedLockTimeouts.cs
- `AzureBlobLeaseDistributedLock` (sentinel blob, `__DistributedLock` metadata) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Azure/AzureBlobLeaseDistributedLock.cs
- `AzureBlobLeaseOptionsBuilder` (15–60s/∞, 30s default, cadence = /3) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Azure/AzureBlobLeaseOptionsBuilder.cs
- `AzureBlobLeaseDistributedLockHandle` (public `LeaseId`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Azure/AzureBlobLeaseDistributedLockHandle.cs
- `MongoDistributedLock` (`findOneAndUpdate` pipeline, `$$NOW`, fencing token) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.MongoDB/MongoDistributedLock.cs
- `MongoDistributedLockHandle` (public `FencingToken`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.MongoDB/MongoDistributedLockHandle.cs
- `MongoIndexInitializer` ("correctness MUST NOT depend on this index") — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.MongoDB/MongoIndexInitializer.cs
- `ZooKeeperConnection` (session pool, 10m max age, `ConnectionWatcher`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.ZooKeeper/ZooKeeperConnection.cs
- `ZooKeeperNodeHandle` (dual-source `HandleLostToken`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.ZooKeeper/ZooKeeperNodeHandle.cs
- `ZooKeeperNodeCreator` (`EPHEMERAL_SEQUENTIAL`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.ZooKeeper/ZooKeeperNodeCreator.cs
- `ZooKeeperDistributedSynchronizationOptionsBuilder` (20s / 15s) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.ZooKeeper/ZooKeeperDistributedSynchronizationOptionsBuilder.cs
- `FileDistributedLock` (`FileShare.None` + `DeleteOnClose`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.FileSystem/FileDistributedLock.cs
- `FileDistributedLockHandle` (`HandleLostToken` → `CancellationToken.None`) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.FileSystem/FileDistributedLockHandle.cs
- `EventWaitHandleDistributedLock` / `DistributedWaitHandleHelpers` (2s abandonment cadence) — https://github.com/madelson/DistributedLock/tree/master/src/DistributedLock.WaitHandles
- Test: `DistributedLockCoreTestCases.TestHandleLostTriggersCorrectly` (the `CanBeCanceled` oracle) — https://github.com/madelson/DistributedLock/blob/master/src/DistributedLock.Tests/AbstractTestCases/DistributedLockCoreTestCases.cs

### Library docs

- Other topics — handle loss, handle abandonment, **"Safety of distributed locking"** — https://github.com/madelson/DistributedLock/blob/master/docs/Other%20topics.md
- SqlServer — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.SqlServer.md
- Postgres — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Postgres.md
- MySql — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.MySql.md
- Oracle — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Oracle.md
- Redis — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Redis.md
- Azure — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.Azure.md
- MongoDB — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.MongoDB.md
- ZooKeeper — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.ZooKeeper.md
- FileSystem — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.FileSystem.md
- WaitHandles — https://github.com/madelson/DistributedLock/blob/master/docs/DistributedLock.WaitHandles.md

### Library issues (maintainer statements quoted above)

- #77 — Named `Mutex` vs `EventWaitHandle`, and file locks over network shares — https://github.com/madelson/DistributedLock/issues/77
- #123 — MySQL < 5.7 one-lock-per-session vs multiplexing — https://github.com/madelson/DistributedLock/issues/123
- #130 — why Redis auto-extension cannot be disabled — https://github.com/madelson/DistributedLock/issues/130
- #147 — Postgres `lock_timeout` race requiring the `pg_locks` recheck — https://github.com/madelson/DistributedLock/issues/147

### Third-party library source

- Npgsql `NpgsqlConnector.GenerateResetMessage` / `DISCARD ALL` on pooled close — includes `SELECT pg_advisory_unlock_all();` — https://github.com/npgsql/npgsql/blob/main/src/Npgsql/Internal/NpgsqlConnector.cs

### Underlying-technology primary docs

**SQL Server / Azure SQL**
- `sp_getapplock` (the `@LockOwner` table, the release-events Remarks, reference counting, the deadlock note) — https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql
- `sp_releaseapplock` — https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-releaseapplock-transact-sql
- SQL Server connection pooling (`Close()` doesn't end the SPID; 4–8 min idle reaping) — https://learn.microsoft.com/en-us/sql/connect/ado-net/sql-server-connection-pooling
- MS-TDS `RESETCONNECTION` status bit ("simulating a logout and a subsequent login") — https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-tds/ce398f9a-7d47-4ede-8f36-9dd6fc21ca43
- **Azure SQL 30-minute gateway idle timeout** — https://learn.microsoft.com/en-us/sql/connect/jdbc/connecting-to-an-azure-sql-database

**PostgreSQL**
- Explicit Locking §13.3.5, Advisory Locks (session vs transaction lifetime; the `LIMIT` dangling-lock warning; the shared-memory cap) — https://www.postgresql.org/docs/current/explicit-locking.html
- Advisory lock functions §9.28.10 (incl. `pg_advisory_unlock_all` "implicitly invoked at session end, even if the client disconnects ungracefully") — https://www.postgresql.org/docs/current/functions-admin.html
- `idle_session_timeout` / `idle_in_transaction_session_timeout` (both default `0` = disabled) — https://www.postgresql.org/docs/current/runtime-config-client.html
- `tcp_keepalives_*` / `tcp_user_timeout` (all default `0` = OS default) — https://www.postgresql.org/docs/current/runtime-config-connection.html
- **PgBouncer feature matrix — "Session-level advisory locks: Transaction pooling — Never"** — https://www.pgbouncer.org/features.html

**MySQL / MariaDB**
- Locking functions 8.4 (`GET_LOCK` timeout is an acquire timeout; not released by commit/rollback) — https://dev.mysql.com/doc/refman/8.4/en/locking-functions.html
- Locking functions 5.7 (**"Before MySQL 5.7, only a single simultaneous lock can be acquired and `GET_LOCK()` releases any existing lock"**) — https://dev.mysql.com/doc/refman/5.7/en/locking-functions.html
- `wait_timeout` 8-hour default — https://dev.mysql.com/doc/refman/8.4/en/gone-away.html
- MariaDB `GET_LOCK` — https://mariadb.com/docs/server/reference/sql-functions/secondary-functions/miscellaneous-functions/get_lock
- MariaDB `wait_timeout` (literal `28800`) — https://mariadb.com/docs/server/ref/mdb/system-variables/wait_timeout/

**Oracle**
- `DBMS_LOCK` (`release_on_commit` default `FALSE`; `ALLOCATE_UNIQUE` `expiration_secs` default 864000 = 10 days and what it actually expires; "User locks are automatically released when a session terminates") — https://docs.oracle.com/en/database/oracle/oracle-database/19/arpls/DBMS_LOCK.html
- `CREATE PROFILE` / `IDLE_TIME` ("The DEFAULT profile initially defines unlimited resources") — https://docs.oracle.com/en/database/oracle/oracle-database/19/sqlrf/CREATE-PROFILE.html
- `RESOURCE_LIMIT` initialisation parameter (defaults `true`) — https://docs.oracle.com/en/database/oracle/oracle-database/19/refrn/RESOURCE_LIMIT.html

**Redis**
- **Distributed Locks with Redis** — the algorithm, the validity formula `MIN_VALIDITY=TTL-(T2-T1)-CLOCK_DRIFT`, the "Disclaimer about consistency" fencing-token/monotonic-clock caveats, "Why Failover-based Implementations Are Not Enough", and the fsync/restart hazard — https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/
  *(Note: the older `develop/use/patterns/…` URL now 404s. Also, as of Redis 8.4 the page gives `DELEX key IFEQ <value>` as the canonical release, with the Lua compare-and-delete as the pre-8.4 path; DistributedLock still uses the Lua form, which remains correct.)*
- Martin Kleppmann, *How to do distributed locking* (efficiency vs correctness; the GC-pause argument; fencing tokens; "you cannot fix this problem by inserting a check on the lock expiry just before writing") — https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html
- antirez, *Is Redlock safe?* (the rebuttal; unique-token-plus-CAS as an alternative to monotonic tokens; the monotonic-clock concession) — http://antirez.com/news/101

**Azure Storage**
- Lease Blob REST reference (15–60s or infinite; the five lease states; renew-after-expiry semantics; **"If the lease ID isn't included, these operations fail on a leased blob, with 412 – Precondition failed"**; the up-to-one-minute wait after an expiry; container-delete exception) — https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob
- Managing concurrency in Blob Storage (read operations are not fenced) — https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage

**Apache ZooKeeper** (3.9 docs)
- Programmer's Guide — Ephemeral Nodes, ZooKeeper Sessions (timeout negotiation, 2×–20× `tickTime`, the partition/expiry timeline, "ZooKeeper doesn't use real time … at all"), Guarantees, Gotchas — https://zookeeper.apache.org/doc/current/zookeeperProgrammers.html
- Recipes — the Locks recipe, "Important Note About Error Handling" (the lost-`create()`-reply GUID problem), the Leader Election note — https://zookeeper.apache.org/doc/current/recipes.html
- Admin Guide — `tickTime` default 2000, `minSessionTimeout` / `maxSessionTimeout` defaults — https://zookeeper.apache.org/doc/current/zookeeperAdmin.html

**MongoDB**
- TTL indexes — the 60-second monitor, "does not guarantee that expired data is deleted immediately", and the unbounded-lag warning — https://www.mongodb.com/docs/manual/core/index-ttl/
- Write operation atomicity (single-document) — https://www.mongodb.com/docs/manual/core/write-operations-atomicity/
- `findAndModify` command (atomicity when modifying a single document) — https://www.mongodb.com/docs/manual/reference/command/findAndModify/
- Aggregation variables — `NOW` — https://www.mongodb.com/docs/manual/reference/aggregation-variables/
- Updates with aggregation pipeline (`$$NOW` usage) — https://www.mongodb.com/docs/manual/tutorial/update-documents-with-aggregation-pipeline/

**.NET / OS file locking**
- `FileShare` enum (documented as if mandatory; no platform notes) — https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare
- `FileOptions.DeleteOnClose` (one sentence, no platform notes) — https://learn.microsoft.com/en-us/dotnet/api/system.io.fileoptions
- **dotnet/runtime `SafeFileHandle.Unix.cs`** — the "only advisory locking … not mandatory, and not atomic with file opening" comment, `FileShare.None` → `LOCK_EX`, the swallowed `ENOTSUP`/`EACCES`, `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`, and the Windows-vs-Unix `DeleteOnClose` comment — https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs
- **dotnet/runtime `pal_io.c`** — `FileSystemSupportsLocking`, the nfs/cifs/smb/smb2 carve-out and the "writes are dropped silently" comment — https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_io.c
- Related runtime issues: https://github.com/dotnet/runtime/issues/44546 and https://github.com/dotnet/runtime/issues/53182
- `flock(2)` man page — advisory locking, the NFS history, and the `local_lock` mount option — https://man7.org/linux/man-pages/man2/flock.2.html
- `_exit(2)` man page — "Any open file descriptors belonging to the process are closed" — https://man7.org/linux/man-pages/man2/exit.2.html
- Terminating a Process (Windows) — "All kernel objects are closed" — https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process

---

## Unverified / open

**Read this section before quoting anything above with confidence.** Items are ordered by how likely they are to
matter.

### Things the vendor docs do NOT say, that people commonly assume they do

1. **Whether `sp_reset_connection` releases Session-owned SQL Server application locks. NOT DOCUMENTED.** This is the
   most load-bearing gap here, because it determines whether a leaked Session applock survives a pooled connection
   being recycled. The inference chain is strong — MS-TDS says `RESETCONNECTION` is *"effectively simulating a logout
   and a subsequent login"*, and `sp_getapplock` says Session locks are *"released when the session is logged out"* —
   but **no Microsoft page states it directly**. The most detailed enumeration available
   ([archived MSDN blog](https://learn.microsoft.com/en-us/archive/blogs/ialonso/misconceptions-around-connection-pooling))
   lists rollback, temp-table drops, cursor cleanup, `CONTEXT_INFO` reset "etc" — and **does not mention application
   locks at all**. Also note the reset fires on the *next command*, not on `Close()`. **If this matters to your
   design, demonstrate it empirically rather than citing it.**

2. **ZooKeeper's docs do NOT warn that the ephemeral-node lock recipe is unsafe.** I went looking for that caveat and
   it is not there. The Recipes page asserts the **opposite** — *"at any snapshot in time no two clients think they
   hold the same lock"* — while the Programmer's Guide separately documents that a partitioned client is not told
   about its own session expiry until it reconnects. The docs never reconcile the two. The documented caveats are
   about something else entirely: the lost-`create()`-reply GUID problem, and a **Leader Election** note that
   *"the znode having no preceding znode on the list of children do not imply that the creator of this znode is aware
   that it is the current leader."* **Do not attribute a lock-safety warning to the ZooKeeper documentation.** The
   tension between the two pages is real and worth pointing at, but it is my reading, not their statement.

3. **Azure does not document any blob-lease clock-skew or time-drift caveat.** I looked at the Lease Blob REST
   reference and the concurrency concepts page and found nothing. The honest framing is that the expiry clock is the
   *service's*, server-side, and Microsoft publishes no client-observable skew model. The nearest documented rough
   edge is availability, not safety: *"a client might need to wait up to one minute before a new lease can be
   acquired"* after an expiry. **Do not claim a documented Azure skew caveat.**

4. **`$$NOW` is not documented as "the server's clock" in those words.** MongoDB says only that it *"returns the
   current datetime value"* and *"returns the same value for all members of the deployment."* Server-side origin
   follows soundly from it being a server-evaluated aggregation variable that the client never supplies — but it is
   **inference, not quotation**, and the per-provider section above flags it as such.

5. **MongoDB never uses the phrase "best-effort" about TTL deletion.** The meaning is carried by *"does not guarantee
   that expired data is deleted immediately"* and *"may exist for some time beyond the 60 second period"*. Quote
   those, not the characterisation. Related: the atomicity guarantee is on the **`findAndModify` command** page and
   the write-operations-atomicity page — **not** on the `db.collection.findOneAndUpdate()` method page, which merely
   says it "Updates a single document."

6. **Neither `FileShare` nor `FileOptions.DeleteOnClose` has any platform note on Microsoft Learn.** Everything in
   this document about Unix advisory `flock`, non-atomicity with `open()`, swallowed `ENOTSUP`/`EACCES`, and
   managed-code `DeleteOnClose` comes from **dotnet/runtime source comments**, not from documentation. That is a
   citation of implementation, not of contract — it could change, though it has been stable for years.

### Discrepancies between DistributedLock's own docs and its source (source wins; both checked on master)

7. **MySQL `KeepaliveCadence` default.** `docs/DistributedLock.MySql.md` says *"Defaults to OFF
   (`Timeout.InfiniteTimeSpan`)"*. The source says `TimeSpan.FromHours(3.5)`, and the method's own XML doc comment
   agrees with the source (*"the default `keepaliveCadence` is 3.5 hours"*). The published doc page is **wrong**. The
   markdown was last touched in 2022 and the option builder has not moved since; I did not trace the full history to
   find which changed first. **Treat 3.5 hours as the real default and verify on your pinned package version.**

8. **MongoDB default collection name.** The doc page prose says locks are stored in `"distributed.locks"` (with a
   dot). The source constant is `internal const string DefaultCollectionName = "distributed_locks";` (underscore).
   Minor, but it will bite anyone querying the collection by hand.

### Things I derived from source but could not empirically verify

9. **The WaitHandles multi-waiter abandonment concern — my inference, not a documented or tested claim.** Recovery
   works by a waiter disposing its handle and re-creating the named event: if the crashed owner was the last holder,
   the kernel object is destroyed and `new EventWaitHandle(initialState: true, …)` makes a fresh **signalled** one.
   But a named kernel object survives as long as **any** handle is open. With two or more concurrent waiters on
   independent 2-second timers, a waiter that disposes while another still holds a handle will simply *open* the
   existing unsignalled object (`createdNew == false`), and the lock stays stuck. Reading
   `CrossProcessAbandonmentHelper`, the library's own test exercises **exactly one waiter**, so the multi-waiter case
   is untested there. I have not reproduced this. **Flagging it as a plausible source-derived hazard, not a
   confirmed bug** — it deserves an experiment before anyone relies on it.

10. **The Azure infinite-duration trap is source-derived and not empirically confirmed.** The chain is solid —
    `TimeoutValue.CompareTo` treats infinite as greatest so `Duration(Timeout.InfiniteTimeSpan)` passes validation;
    `RenewalCadence` then defaults to infinite; `MonitoringCadence` becomes the infinite `Duration`; and
    `LeaseMonitor`'s loop opens with `await Task.Delay(-1, disposalToken)` so the body never runs. I have **not** run
    it. If you are considering an infinite Azure lease, test the abandonment path first.

11. **Redis `HandleLostToken` lag under a real GC pause.** My claim that the token fires only *after* the process
    resumes follows directly from `LeaseMonitor` using a local `Stopwatch` and a `Task.Delay` loop inside the same
    process, but I have not measured it under an induced stop-the-world pause.

### Smaller unverified details

12. **MySQL's literal `28800` for `wait_timeout`.** "Eight hours" is verified from MySQL's own docs; the numeric
    value was only confirmed on **MariaDB's** variable page. MySQL's own system-variable reference page was too large
    to retrieve in full.
13. **The MariaDB version that introduced multiple simultaneous `GET_LOCK`s** (commonly cited as 10.0.2 / MDEV-3917).
    MariaDB's current `GET_LOCK` page carries no version note. The **MySQL 5.7** statement is fully verified; the
    MariaDB one is not.
14. **Oracle's `DEFAULT` profile listing `IDLE_TIME UNLIMITED` specifically.** Covered by the general *"The DEFAULT
    profile initially defines unlimited resources"*, but not named individually. Confirm on your instance with
    `SELECT limit FROM dba_profiles WHERE profile='DEFAULT' AND resource_name='IDLE_TIME'`.
15. **Version drift generally.** Everything about the library is pinned to **release 2.8.3 / commit `8007a86`**.
    Each DistributedLock package is versioned **independently**, so "2.8.3" is the meta-package; the defaults quoted
    here should be re-checked against the specific provider package versions you actually reference.
