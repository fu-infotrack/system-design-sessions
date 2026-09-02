# Locking Resources

Curated during the research behind [`../01-locking/`](../01-locking/). Full
findings, with the primary-source quotes, live in
[`../01-locking/research/`](../01-locking/research/).

## Knowledge

- [Martin Kleppmann — "How to do distributed locking"](https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html)
  The efficiency-vs-correctness split, the GC-pause timeline, and the fencing-token
  argument. Use for: why a lock with a timeout cannot be relied on for correctness.
- [antirez — "Is Redlock safe?"](http://antirez.com/news/101)
  The reply. Use for: the counter-argument that a resource able to reject a stale
  token is already a linearizable store. Read *with* Kleppmann, never instead of.
- [redis.io — Distributed locks pattern](https://redis.io/docs/latest/develop/use/patterns/distributed-locks/)
  Canonical `SET NX PX` + Lua compare-and-delete. Now says "You should implement
  fencing tokens" in Redis's own voice. Use for: the correct single-instance recipe.
- [PostgreSQL — Explicit Locking (Advisory Locks)](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS)
  Session vs transaction scope, the `_shared` and `_try` variants. Use for: anything
  advisory-lock shaped.
- [Microsoft Learn — Lease Blob (REST)](https://learn.microsoft.com/en-us/rest/api/storageservices/lease-blob)
  The lease-state outcome table is the useful part. Use for: whether a blob lease
  actually fences a write.
- [Microsoft Learn — Service Bus message transfers, locks, settlement](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-transfers-locks-settlement)
  `LockDuration`, peek-lock, and Microsoft's own conclusion that idempotent handling
  is critical. Use for: the lease our team is most likely to meet first.
- [madelson/DistributedLock](https://github.com/madelson/DistributedLock)
  Ten providers behind one interface, plus `HandleLostToken`. Use for: don't
  hand-roll a distributed lock, and for comparing provider semantics.
- [KIP-62](https://cwiki.apache.org/confluence/display/KAFKA/KIP-62%3A+Allow+consumer+to+send+heartbeats+from+a+background+thread) and [KIP-447](https://cwiki.apache.org/confluence/display/KAFKA/KIP-447%3A+Producer+scalability+for+exactly+once+semantics)
  Why `max.poll.interval.ms` is a lease, and Kafka's own "Fence Zombie" section.
  Use for: whether partitioning removes the need for a lock.

## Wisdom (Communities)

- [r/ExperiencedDevs](https://reddit.com/r/ExperiencedDevs)
  Good signal on design trade-offs rather than syntax. Use for: sanity-checking a
  locking design against people who have run one in production.
- [Postgres Slack — #general / #performance](https://postgres-slack.herokuapp.com/)
  Use for: advisory-lock and isolation-level questions where the answer depends on
  version specifics.
- Internal: the team's own code review.
  The highest-value community here is the one that already exists. A lesson has
  landed when someone asks *"what invariant is this protecting?"* in a PR.

## Gaps

- **`librdkafka` / `Confluent.Kafka` revocation behaviour.** All the Kafka research
  is the Java client. KIP-735 notes librdkafka enforces the session timeout locally
  with different revocation behaviour. This is our client, and it is unverified.
- **A .NET-specific community with high signal on concurrency.** Nothing found that
  beats the general ones above.
