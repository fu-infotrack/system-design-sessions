# Q4 — The Canonical Single-Instance Redis Lock

## Summary

redis.io specifies exactly one correct single-instance lock: `SET resource_name my_random_value NX PX 30000` to
acquire, and a compare-and-delete to release. The value must be a **unique random token** — not because the lock
needs a payload, but because release must be conditional on ownership; redis.io recommends 20 bytes from
`/dev/urandom`. Release is done with the `DELEX key IFEQ my_random_value` command (Redis 8.4+) or, on earlier
versions, a three-line Lua script that deletes the key only if its value matches. A plain `DEL` is unsafe because a
client that overran its TTL will delete a lock that a *different* client has since acquired. redis.io also states
plainly that this single-instance approach cannot survive failover to a replica — Redis replication is asynchronous,
so a lock acknowledged by a master that then crashes is simply lost — and it explicitly discourages the legacy
`SETNX` + timestamp pattern.

---

## Findings

### The acquire command, verbatim

Source: https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/

> To acquire the lock, the way to go is the following:
>
> ```
> SET resource_name my_random_value NX PX 30000
> ```
>
> The command will set the key only if it does not already exist (`NX` option), with an expire of 30000 milliseconds
> (`PX` option). The key is set to a value "my_random_value". This value must be unique across all clients and all
> lock requests.

The `SET` command page states the same shape in seconds form —
https://redis.io/docs/latest/commands/set/ :

> The command `SET resource-name anystring NX EX max-lock-time` is a simple way to implement a locking system with
> Redis. A client can acquire the lock if the above command returns `OK` (or retry after some time if the command
> returns Nil), and remove the lock just using `DEL`. The lock will be auto-released after the expire time is reached.

...and then immediately hardens it:

> It is possible to make this system more robust modifying the unlock schema as follows:
>
> * Instead of setting a fixed string, set a non-guessable large random string, called token.
> * Instead of releasing the lock with `DEL`, send a script that only removes the key if the value matches.

### Why the value must be a unique random token, and how to generate it

Source: https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/

> Basically the random value is used in order to release the lock in a safe way, with a script that tells Redis:
> remove the key only if it exists and the value stored at the key is exactly the one I expect to be.

On generation, verbatim:

> What should this random string be? We assume it's 20 bytes from `/dev/urandom`, but you can find cheaper ways to
> make it unique enough for your tasks. For example a safe pick is to seed RC4 with `/dev/urandom`, and generate a
> pseudo random stream from that. A simpler solution is to use a UNIX timestamp with microsecond precision,
> concatenating the timestamp with a client ID. It is not as safe, but probably sufficient for most environments.

Note the ordering of preference: `/dev/urandom` (20 bytes) is the reference answer; timestamp+client-ID is described
as "not as safe, but probably sufficient". The `SET` page phrases the requirement as "a non-guessable large random
string".

### The release: `DELEX` (Redis 8.4+) and the Lua script

Source: https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/

> This is accomplished with the following command:
>
> ```
> DELEX key IFEQ my_random_value
> ```
>
> The `DELEX` command was introduced in Redis 8.4. On previous Redis versions, this could be accomplished with the
> following Lua script:

The Lua script, quoted **verbatim** as it appears on the distributed-locks page:

```lua
if redis.call("get",KEYS[1]) == ARGV[1] then
    return redis.call("del",KEYS[1])
else
    return 0
end
```

The `SET` command page carries the same script with `then` on its own line, and adds the invocation —
https://redis.io/docs/latest/commands/set/ :

```lua
if redis.call("get",KEYS[1]) == ARGV[1]
then
    return redis.call("del",KEYS[1])
else
    return 0
end
```

> The script should be called with `EVAL ...script... 1 resource-name token-value`

(Both renderings are semantically identical; the distributed-locks page is the more canonical of the two for lock
purposes. Kleppmann's own summary of the correct primitive matches: "conditional set-if-not-exists to obtain a lock,
atomic delete-if-value-matches to release a lock" — https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html)

### Why plain `DEL` is wrong — the exact interleaving

Source: https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/

> This is important in order to avoid removing a lock that was created by another client. For example a client may
> acquire the lock, get blocked performing some operation for longer than the lock validity time (the time at which
> the key will expire), and later remove the lock, that was already acquired by some other client. Using just `DEL`
> is not safe as a client may remove another client's lock. With the `DELEX` command or the above script instead
> every lock is "signed" with a random string, so the lock will be removed only if it is still the one that was set
> by the client trying to remove it.

Spelled out as a timeline (derived directly from that paragraph, not quoted):

1. Client A runs `SET lock A-token NX PX 30000` → OK. A holds the lock.
2. A stalls — GC pause, page fault, slow downstream call — for longer than 30 s.
3. The key expires. Redis auto-releases the lock.
4. Client B runs `SET lock B-token NX PX 30000` → OK. **B now holds the lock.**
5. A wakes up, finishes its work, and calls `DEL lock` in its `finally` block.
6. A has just deleted **B's** lock. The resource is now unprotected while B still believes it is protected, and a
   third client C can acquire immediately.

The compare-and-delete closes step 5-6: A's `DELEX lock IFEQ A-token` (or the Lua script with `ARGV[1] = A-token`)
sees `B-token` at the key, matches nothing, and returns 0 without deleting. Note that this fixes the *release*
hazard only — A's work in step 5 is still unprotected. That is the residual problem the fencing-token debate is about
(see `03-redlock-debate.md`).

### The `SETNX` + expire legacy pattern is explicitly discouraged

Source: https://redis.io/docs/latest/commands/setnx/ ("Design pattern: locking with `SETNX`")

> **Please note that:**
>
> 1. The following pattern is discouraged in favor of [the Redlock algorithm] which is only a bit more complex to
>    implement, but offers better guarantees and is fault tolerant.
> 2. We document the old pattern anyway because certain existing implementations link to this page as a reference.
>    Moreover it is an interesting example of how Redis commands can be used in order to mount programming primitives.
> 3. Anyway even assuming a single-instance locking primitive, starting with 2.6.12 it is possible to create a much
>    simpler locking primitive, equivalent to the one discussed here, using the `SET` command to acquire the lock,
>    and a simple Lua script to release the lock. The pattern is documented in the `SET` command page.

The legacy pattern it documents (and discourages) is:

> ```
> SETNX lock.foo <current Unix time + lock timeout + 1>
> ```
>
> If `SETNX` returns `1` the client acquired the lock, setting the `lock.foo` key to the Unix time at which the lock
> should no longer be considered valid. The client will later use `DEL lock.foo` in order to release the lock.

And the race that makes the naive expired-lock cleanup unsafe — verbatim:

> When this happens we can't just call `DEL` against the key to remove the lock and then try to issue a `SETNX`, as
> there is a race condition here, when multiple clients detected an expired lock and are trying to release it.
>
> * C1 and C2 read `lock.foo` to check the timestamp, because they both received `0` after executing `SETNX`, as the
>   lock is still held by C3 that crashed after holding the lock.
> * C1 sends `DEL lock.foo`
> * C1 sends `SETNX lock.foo` and it succeeds
> * C2 sends `DEL lock.foo`
> * C2 sends `SETNX lock.foo` and it succeeds
> * **ERROR**: both C1 and C2 acquired the lock because of the race condition.

Key structural point: the legacy pattern is unsafe because `SETNX` and the expiry are **two separate operations** —
the key's "expiry" is application-interpreted data in the value, not a server-side TTL, so a client that dies between
`SETNX` and setting the timeout leaves a lock with no expiry at all. `SET key val NX PX ms` (available since Redis
2.6.12) makes acquisition and expiry a single atomic command, which is the whole reason it superseded the pattern.

Note also that the `SET` page's own "Patterns" section carries the same steer:

> Note: The following pattern is discouraged in favor of [the Redlock algorithm] which is only a bit more complex to
> implement, but offers better guarantees and is fault tolerant.

So redis.io discourages the *single-instance* pattern in favour of Redlock. Kleppmann recommends the opposite for
efficiency locks ("Don't bother with setting up a cluster of five Redis nodes"). Present that as a genuine
difference of opinion between two primary sources rather than as a settled question.

### Yes — redis.io states the single-instance lock is NOT safe under failover to a replica

Source: https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/ ("Why Failover-based Implementations
Are Not Enough"). Quoted verbatim:

> Superficially this works well, but there is a problem: this is a single point of failure in our architecture. What
> happens if the Redis master goes down? Well, let's add a replica! And use it if the master is unavailable. This is
> unfortunately not viable. By doing so we can't implement our safety property of mutual exclusion, because **Redis
> replication is asynchronous**.
>
> There is a race condition with this model:
>
> 1. Client A acquires the lock in the master.
> 2. The master crashes before the write to the key is transmitted to the replica.
> 3. The replica gets promoted to master.
> 4. Client B acquires the lock to the same resource A already holds a lock for. **SAFETY VIOLATION!**

And the guidance that follows:

> Sometimes it is perfectly fine that, under special circumstances, for example during a failure, multiple clients
> can hold the lock at the same time. If this is the case, you can use your replication based solution. Otherwise we
> suggest to implement the solution described in this document.

This is worth being precise about: the failure is not "the single instance". A truly single, always-available
instance is safe — redis.io says so directly:

> So now we have a good way to acquire and release the lock. With this system, reasoning about a non-distributed
> system composed of a single, always available, instance, is safe.

The failure is **adding a replica and failing over to it**, because the replication is asynchronous. Sentinel and
managed Redis offerings that do automatic failover put you in exactly this case.

### Definition of "lock validity time"

Source: same page.

> The "lock validity time" is the time we use as the key's time to live. It is both the auto release time, and the
> time the client has in order to perform the operation required before another client may be able to acquire the
> lock again, without technically violating the mutual exclusion guarantee, which is only limited to a given window
> of time from the moment the lock is acquired.

That last clause is the load-bearing one: mutual exclusion is scoped to a window, by design.

---

## Talk-ready points

- "There is exactly one line to remember: `SET resource_name my_random_value NX PX 30000`. `NX` means set only if it
  doesn't exist — that's the mutual exclusion. `PX 30000` is a 30-second TTL — that's the deadlock protection. Both
  in one atomic command."
- "The value isn't decoration. The value is your proof of ownership. redis.io: 'This value must be unique across all
  clients and all lock requests.' The reference answer is 20 bytes from `/dev/urandom`."
- "Releasing with `DEL` is the bug everyone ships. Here's the interleaving: A takes a 30-second lock. A stalls for 40
  seconds. The lock expires. B takes the lock. A wakes up, hits its `finally` block, calls `DEL` — and deletes *B's*
  lock. Now nobody's protecting anything and C can walk straight in."
- "The fix is compare-and-delete, and it has to be atomic, which is why it's a Lua script — get-then-delete from the
  client has the same race one layer up."
- Show the script and say it out loud: "if get(key) equals my token, delete it, otherwise return zero."
- "On Redis 8.4 and later there's a first-class command for this: `DELEX key IFEQ my_random_value`. Same semantics,
  no script."
- "If you see `SETNX` plus a separate `EXPIRE` in our codebase, that's the pre-2.6.12 pattern. redis.io's own SETNX
  page says the pattern is 'discouraged'. The problem is that it's two operations — die in between and you've
  created a lock with no expiry, i.e. a permanent outage."
- "And the one that catches people in production: this lock does **not** survive failover. redis.io spells it out —
  'Redis replication is asynchronous'. A acquires on the master, the master dies before the key replicates, the
  replica is promoted, B acquires the same lock. Their word for it, in bold, is 'SAFETY VIOLATION'."
- "So if you're on managed Redis with automatic failover, you already have the failure mode Redlock was invented to
  address. Whether Redlock is the right answer is a separate argument — see the Kleppmann/antirez slide."
- "redis.io's own definition of lock validity time is honest about the limit: mutual exclusion is 'only limited to a
  given window of time from the moment the lock is acquired'. Overrun the window and you have no lock, just
  optimism."

---

## Sources

- https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/ — redis.io, "Distributed Locks with
  Redis". The canonical page: the `SET ... NX PX` acquire form, the random-token requirement and generation advice,
  `DELEX`, the Lua unlock script, the why-`DEL`-is-wrong paragraph, the async-replication failover race, and the
  definition of lock validity time.
- https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/index.html.md — machine-readable Markdown
  rendering of the same page.
- https://redis.io/docs/latest/commands/setnx/ — `SETNX` command reference, including the "Design pattern: locking
  with `SETNX`" section that documents and explicitly discourages the legacy pattern, and the C1/C2 double-acquire race.
- https://redis.io/docs/latest/commands/set/ — `SET` command reference; its "Patterns" section carries the
  `SET resource-name anystring NX EX max-lock-time` form, the token hardening advice, the Lua unlock script, and the
  `EVAL ...script... 1 resource-name token-value` invocation.
- https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html — secondary for this file, cited only
  for his endorsement of this exact single-instance primitive for efficiency locks.

---

## Unverified / open

- **`DELEX` availability in our deployments.** redis.io says "The `DELEX` command was introduced in Redis 8.4." I
  have not checked which Redis version the team runs, nor whether Azure Cache for Redis / Redis Cloud tiers expose
  it. Verify before recommending it over the Lua script.
- **Client-library behaviour.** I did not check whether StackExchange.Redis, `DistributedLock.Redis`, or any other
  client we use actually implements compare-and-delete release rather than plain `DEL`. Worth confirming before
  claiming our locks are safe on release.
- **Whether redis.io recommends a specific retry backoff distribution.** The page says only "it should try again
  after a random delay in order to try to desynchronize multiple clients" — no concrete range or distribution is
  specified. Do not quote a number as official.
- **Redis Cluster.** The page covers a single instance and N independent masters. It does not discuss running the
  lock key on a Redis Cluster (where a slot can be resharded/failed over), and I did not research that case.
