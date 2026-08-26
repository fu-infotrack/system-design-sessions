# Kafka Partitioning as a Substitute for Distributed Locking

## Summary

Kafka's exclusivity guarantee is real, precisely stated, and narrower than the framework's claim: within one consumer
group each partition is assigned to **exactly one consumer** — Apache's own words are "each of which is consumed by
exactly one consumer within each subscribing consumer group at any given time." But that guarantee is about
**assignment**, not about **processing**. Ownership of a partition is held on a renewable lease whose deadline is
`max.poll.interval.ms` (default **300 000 ms / 5 minutes** in Kafka 4.3); when a consumer overruns it, KIP-62
specifies that a *background thread* stops heartbeating and "send[s] an explicit LeaveGroup request" while the
application thread is still inside its processing call — nothing interrupts the work in flight. The coordinator then
reassigns the partition and the new owner resumes from the **last committed offset**, so two processes can be
executing the same record simultaneously. This is not an inferred edge case: KIP-447's "Fence Zombie" section walks
through exactly this scenario as the motivation for its design. The one thing Kafka does fence is the **offset
commit** — the stale member's commit is rejected on generation id (classic) or member epoch (KIP-848) — but a
rejected commit does not roll back an HTTP POST, a database write, or an email that the stale member already
performed. So "structurally impossible" survives only for the steady state; the residual window is a lease-expiry
window with a five-minute default TTL, materially the same failure shape as a Redis lock TTL expiring mid-work, and
the framework should say partitioning removes **most** of the need for a lock and converts the remainder into an
idempotency requirement.

---

## The guarantees

These are the parts that are reliably, quotably true. All quotes are Apache Kafka 4.3 (current release; docs last
modified 2026-05-22) unless labelled otherwise.

### Q1a — One partition, one consumer, per group

The design documentation states it as an invariant, not a best effort —
https://kafka.apache.org/43/design/design/ , "Consumer Position":

> Our topic is divided into a set of totally ordered partitions, **each of which is consumed by exactly one consumer
> within each subscribing consumer group at any given time**. This means that the position of a consumer in each
> partition is just a single integer, the offset of the next message to consume.

The `KafkaConsumer` javadoc repeats it in mechanical terms —
https://kafka.apache.org/43/javadoc/org/apache/kafka/clients/consumer/KafkaConsumer.html :

> Kafka will deliver each message in the subscribed topics to one process in each consumer group. This is achieved by
> balancing the partitions between all members in the consumer group so that **each partition is assigned to exactly
> one consumer in the group**. So if there is a topic with four partitions, and a consumer group with two processes,
> each process would consume from two partitions.

KIP-429 states the *reason* the old protocol had to revoke everything before reassigning anything, which is the
clearest statement of the intended invariant — https://cwiki.apache.org/confluence/display/KAFKA/KIP-429 :

> The reason for revoking all ongoing tasks is because **we need to guarantee each topic partition is assigned with
> exactly one consumer at any time**. In this way, any topic partition could not be re-assigned before it is revoked.

And KIP-429's compatibility section names the failure it is guarding against — worth noting, because it shows Kafka
itself treats "two owners" as a hazard to be designed against rather than a physical impossibility:

> ...rather than letting them to fall into an undefined behavior or even worse, **having some partitions to be owned
> by more than one member**.

### Q1b — Different groups are independent

https://kafka.apache.org/43/javadoc/.../KafkaConsumer.html :

> Conceptually you can think of a consumer group as being a single logical subscriber that happens to be made up of
> multiple processes. As a multi-subscriber system, Kafka naturally supports having any number of consumer groups for
> a given topic **without duplicating data** (additional consumers are actually quite cheap).

> To get semantics similar to pub-sub in a traditional messaging system each process would have its own consumer
> group, so each process would subscribe to all the records published to the topic.

**Consequence for the framework:** the exclusivity is scoped to a `group.id`. Two different services consuming the
same topic under two different group ids will both process every record for key K, concurrently. Partitioning gives
you mutual exclusion *inside one group only*. Any claim of "only one processor for this key" that spans consumer
groups, or spans a consumer group and some unrelated code path (a REST endpoint, a nightly batch, a support script)
is unsupported by anything in this document.

### Q1c — Excess consumers are idle

Apache does not state this in one sentence, but it follows directly from "each partition is assigned to exactly one
consumer" and is confirmed by the contrast Apache draws with **share groups** (a different group type, added for
precisely this limitation) — https://kafka.apache.org/43/design/design/ , "The Share Consumer":

> The fundamental differences between a share group and a consumer group are:
> - The consumers in a share group cooperatively consume records, and **partitions may be assigned to multiple
>   consumers**.
> - **The number of consumers in a share group can exceed the number of partitions in a topic.**

Confluent states the consumer-group case explicitly. *Secondary source (Confluent official, developer.confluent.io
course "Apache Kafka Internal Architecture — Consumer Group Protocol")*:

> The unit of parallelism is the partition. For a given consumer group, consumers can process more than one partition
> but **a partition can only be processed by one consumer**. If our group is subscribed to two topics and each one has
> two partitions then we can effectively use up to four consumers in the group. **We could add a fifth but it would
> sit idle since partitions cannot be shared.**

Note a wrinkle that matters if you actually rely on N+1: with the **default** `RangeAssignor`, idleness can happen
even when consumers ≤ total partitions, because Range assigns per-topic. *Same Confluent source*:

> This strategy goes through each topic in the subscription and assigns each of the partitions to a consumer, starting
> at the first consumer. ... **If no single topic in the subscription has as many partitions as there are consumers,
> then some consumers will be idle.**

### Q1d — Ordering is per-partition only

https://kafka.apache.org/43/getting-started/introduction/ :

> Events with the same event key (e.g., a customer or vehicle ID) are written to the same partition, and **Kafka
> guarantees that any consumer of a given topic-partition will always read that partition's events in exactly the same
> order as they were written**.

There is no cross-partition ordering guarantee, stated or implied, anywhere. So "partition by key" buys you a total
order **per key** (given a stable partitioner and a stable partition count) and nothing across keys.

One caveat the framework should carry: this guarantee is anchored to the partition, not the key. Changing a topic's
partition count re-maps keys to partitions, and the group "will automatically detect the new partitions through
periodic metadata refreshes and assign them to members of the group" (KafkaConsumer javadoc). Records for key K
written before and after the change can land in two different partitions, which can be owned by two different
consumers, at the same time. Adding partitions is therefore a **correctness event**, not just a capacity event, for
anyone using partitioning as a lock.

---

## Partition ownership as a lease

This is the central finding. Read it against the Redis TTL section in `04-redis-canonical-lock.md` — the shapes are
the same.

### The mechanism, from primary sources

There are two independent liveness deadlines, and KIP-62 is the document that split them —
https://cwiki.apache.org/confluence/display/KAFKA/KIP-62 :

> **Decoupling the processing timeout:** We propose to introduce a separate locally enforced timeout for record
> processing and **a background thread to keep the session active** until this timeout expires. We call this new
> timeout as the "process timeout" and expose it in the consumer's configuration as `max.poll.interval.ms`. This
> config sets the maximum delay between client calls to `poll()`. **When the timeout expires, the consumer will stop
> sending heartbeats and send an explicit LeaveGroup request.** As soon as the consumer resumes processing with
> another call to `poll()`, the consumer will rejoin the group.

And, explicitly, which thread does that:

> **The background thread is only allowed to send heartbeats in order to keep the member alive and to send the
> explicit LeaveGroup if the processing timeout expires.**

That single sentence is the whole answer to Q3. The LeaveGroup is emitted by a thread that is *not* the thread doing
your work. Nothing in the client stops, interrupts, cancels, or even notifies the application thread that is
mid-`process(X)`. It keeps going.

The `KafkaConsumer` javadoc describes the same thing from the user's side —
https://kafka.apache.org/43/javadoc/.../KafkaConsumer.html , "Detecting Consumer Failures":

> It is also possible that the consumer could encounter a "livelock" situation where it is continuing to send
> heartbeats, but no progress is being made. To prevent the consumer from holding onto its partitions indefinitely in
> this case, we provide a liveness detection mechanism using the `max.poll.interval.ms` setting. Basically if you
> don't call poll at least as frequently as the configured max interval, then **the client will proactively leave the
> group so that another consumer can take over its partitions**. When this happens, you may see an offset commit
> failure (as indicated by a `CommitFailedException` thrown from a call to `commitSync()`). **This is a safety
> mechanism which guarantees that only active members of the group are able to commit offsets.**

Note carefully what that last sentence promises and what it does not. The safety mechanism is on **offset commits**.
It is not on processing.

The consumer config reference says the same for the timeout itself —
https://kafka.apache.org/43/configuration/consumer-configs/ , `max.poll.interval.ms`:

> The maximum delay between invocations of `poll()` when using consumer group management. This places an upper bound
> on the amount of time that the consumer can be idle before fetching more records. **If `poll()` is not called before
> expiration of this timeout, then the consumer is considered failed and the group will rebalance in order to reassign
> the partitions to another member.**

### The timeline

Setup: topic `payments`, key K hashes to partition P. Consumer A owns P. Consumer B is in the group. Classic
protocol, all defaults, `enable.auto.commit=false`, manual `commitSync()` after processing.

| t | What happens |
|---|---|
| `0` | A calls `poll()`, gets a batch of up to `max.poll.records` = **500** records including record X for key K. **The lease starts.** Deadline = `t + max.poll.interval.ms` = **t + 300 000 ms (5 min)**. |
| `0` .. | A processes the batch on the application thread. The background heartbeat thread heartbeats every `heartbeat.interval.ms` = **3 000 ms**. Ownership is renewed automatically — A does not have to do anything. |
| `~200 s` | A is slow. Pick your cause: a long stop-the-world GC, a downstream HTTP call with no timeout, an unlucky 500-record batch, a `SELECT` that started table-scanning after a plan flip. A is *alive and heartbeating* the whole time, so `session.timeout.ms` is never breached. |
| `300 000 ms` | **The lease expires.** Per KIP-62 the background thread stops heartbeating and sends an explicit `LeaveGroup`. A's application thread is still inside `process(X)`. It receives no signal. |
| `300 000 ms + ε` | The coordinator removes A, bumps the group generation, and starts a rebalance. |
| `~300 001 ms` | Rebalance completes. P is assigned to B. B fetches P from the **last committed offset**, which is at or before X. B begins `process(X)`. |
| **overlap** | **A and B are both executing `process(X)`.** A believes it owns P. It does not. There is nothing in Kafka that tells it otherwise until it next talks to the coordinator. |
| later | A finishes. It performs its side effect — POSTs to the payment gateway / `UPDATE`s the row / sends the email — and *then* calls `commitSync()`. |
| later | The commit is **rejected**. A gets `CommitFailedException`. **The side effect is not rolled back.** |
| later | A calls `poll()`, rejoins as a new member, is given some assignment, and re-fetches from the committed offset. It may reprocess X itself. |

### Is that fairly called a lease/TTL? Yes.

Line it up against `04-redis-canonical-lock.md`:

| | Redis `SET k tok NX PX 30000` | Kafka partition ownership |
|---|---|---|
| What bounds ownership | `PX` TTL | `max.poll.interval.ms` |
| Default / typical | 30 000 ms (redis.io's own example) | **300 000 ms** |
| Renewal | you write it yourself, or you don't | automatic, by a background thread |
| On expiry, can a second holder start? | yes, immediately | yes, after one rebalance round |
| Is the first holder stopped? | **no** | **no** |
| Does the first holder know? | not until it tries to release | not until it tries to commit |
| Is the stale release/commit rejected? | yes — compare-and-delete on the random token | yes — generation id / member epoch |
| Does the rejection undo side effects? | **no** | **no** |

redis.io's own framing of the limit reads almost as a description of `max.poll.interval.ms`: mutual exclusion is
"only limited to a given window of time from the moment the lock is acquired." Kafka's window is the poll interval.
Overrun it and you have no lock, just optimism.

Two honest differences, both in Kafka's favour, neither of which closes the hole:

1. **The renewal is free and it is decoupled from the work.** With Redis you must run your own watchdog and most
   people don't. With Kafka a background thread holds the lease for you across a 4-minute GC pause. That makes
   accidental expiry *much rarer* than with a hand-rolled Redis lock, and it makes the default lease long. It does not
   change what happens when the lease does expire.
2. **The fencing token is already threaded into the one write that matters.** Kleppmann's objection to Redlock is that
   a fencing token is useless unless the protected resource checks it, and applications almost never plumb it through.
   Kafka plumbs it automatically — the generation id / member epoch rides on every `OffsetCommitRequest`, so the
   *offset* is genuinely fenced with no work from you. But the offset is a Kafka-internal resource. Your database row
   is not fenced, because nothing carries the epoch there unless you carry it.

And one difference that is *worse* than Redis: the lease is held **per partition, not per key**. One slow record for
one key starves every other key on that partition and, on expiry, hands the whole partition to another consumer. The
blast radius of a single overrun is the whole partition.

### Q2 — Hot/cold standby, and which knob governs which failure

Running N+1 consumers against N partitions does give you a warm process that will be handed a partition on the next
rebalance. Calling it a "free hot standby" overstates it on three counts: the extra process is a full group member
that participates in every rebalance; it holds no pre-warmed state (see the Kafka Streams note below); and takeover
is not instant, it costs a failure-detection delay plus a rebalance.

Three failure modes, three different deadlines. Getting these confused is the most common source of wrong takeover
estimates.

**1. Graceful shutdown (`consumer.close()`, SIGTERM handled).** The consumer sends `LeaveGroup`; the coordinator
rebalances immediately. Takeover ≈ one rebalance round. *Secondary source (Confluent official,
docs.confluent.io/platform/current/clients/consumer.html)*: "For normal shutdowns, however, the consumer sends an
explicit request to the coordinator to leave the group which triggers an immediate rebalance." **Exception:** static
members do not send `LeaveGroup` — see below.

**2. Process killed / host lost / network partition (heartbeats stop).** Governed by **`session.timeout.ms`**
(default **45 000 ms**; broker-side `group.consumer.session.timeout.ms`, also **45 000 ms**, under the KIP-848
protocol). Config reference, verbatim:

> The timeout used to detect client failures when using Kafka's group management facility. The client sends periodic
> heartbeats to indicate its liveness to the broker. **If no heartbeats are received by the broker before the
> expiration of this session timeout, then the broker will remove this client from the group and initiate a
> rebalance.**

Takeover ≈ up to 45 s + one rebalance. `heartbeat.interval.ms` (**3 000 ms**) does **not** set this deadline
— it sets how often liveness is proven and how quickly a member learns a rebalance is needed. Config reference: "the
value must be set lower than `session.timeout.ms`, but typically should be set no higher than 1/3 of that value."

**3. Process alive but stuck (livelock).** Governed by **`max.poll.interval.ms`** (default **300 000 ms**). This is
the mode that produces the overlap above, because the process is still running. Takeover ≈ up to 5 min + one
rebalance, on defaults.

**The distinction that matters for correctness:** in mode 2 the old owner is dead, so there is no concurrent
processing — but partial side effects may already have landed before the crash, which is ordinary at-least-once, not a
concurrency bug. In mode 3 the old owner is **alive and working**. Only mode 3 gives you two live processors on the
same record. This is exactly the Redis distinction between "the holder crashed" and "the holder is slow".

**Static membership (`group.instance.id`, KIP-345) changes the timing, not the hazard.** From the consumer config
reference for `max.poll.interval.ms`, verbatim:

> **For consumers using a non-null `group.instance.id` which reach this timeout, partitions will not be immediately
> reassigned. Instead, the consumer will stop sending heartbeats and partitions will be reassigned after expiration of
> the session timeout** (defined by the client config `session.timeout.ms` if using the Classic rebalance protocol, or
> by the broker config `group.consumer.session.timeout.ms` if using the Consumer protocol). This mirrors the behavior
> of a static consumer which has shutdown.

So with static membership the stuck consumer loses ownership at 300 s (it stops heartbeating) but B does not take over
until roughly 345 s. That *narrows the overlap window* by the session timeout and *widens the unprocessed window* by
the same amount. It is a different trade, not a fix — and it introduces a new gap in which nobody owns the partition
while A is still writing.

KIP-345 also removes the graceful-shutdown fast path: "consumer with `group.instance.id` set will **not send leave
group request** when they go offline, which means we shall only rely on `session.timeout` to trigger group rebalance."
So a rolling deploy of static members costs a session timeout per instance unless you drive `RemoveMemberFromGroup`
yourself. And static membership adds its own fencing: duplicate `group.instance.id` values produce
`FencedInstanceIdException` — "a fencing mechanism on broker side will inform your duplicate client to shutdown
immediately." That fences a *misconfigured duplicate identity*, not a slow processor.

**Kafka Streams `num.standby.replicas` is a different concept and does not help here.** Default **0**. It is about
state-store restore latency, not about processing exclusivity —
https://kafka.apache.org/43/streams/developer-guide/config-streams/ :

> The number of standby replicas. **Standby replicas are shadow copies of local state stores.** Kafka Streams attempts
> to create the specified number of replicas per store and keep them up to date as long as there are enough instances
> running. **Standby replicas are used to minimize the latency of task failover.**

A standby task does not process input records; it replays a changelog so that if the active task moves here, it does
not have to rebuild state from scratch. Setting it to 1 makes failover *faster*; it does not make the active task
exclusive in any sense that partition assignment doesn't already provide. Do not cite it as a locking mechanism.

---

## Config reference

All values from Apache Kafka **4.3** official documentation (docs last modified 2026-05-22). Client configs from
https://kafka.apache.org/43/configuration/consumer-configs/ , broker configs from
https://kafka.apache.org/43/configuration/broker-configs/ , Streams from
https://kafka.apache.org/43/configuration/kafka-streams-configs/ .

### Consumer client

| Setting | Type | Default (4.3) | Governs |
|---|---|---|---|
| `group.protocol` | string | **`classic`** | Which rebalance protocol the client uses. `consumer` = KIP-848. Not default until Kafka 5.0. |
| `max.poll.interval.ms` | int | **300000 (5 min)** | **The ownership lease.** Exceed it and the client leaves the group; partitions are reassigned. Applies under both protocols. |
| `session.timeout.ms` | int | **45000 (45 s)** | Heartbeat-loss detection. **Classic protocol only** — unsupported when `group.protocol=consumer`. |
| `heartbeat.interval.ms` | int | **3000 (3 s)** | How often liveness is proven / rebalance is noticed. **Classic only.** Not a detection deadline. |
| `max.poll.records` | int | **500** | Records per `poll()`. Directly sets how much work must fit inside the lease. |
| `partition.assignment.strategy` | list | **`RangeAssignor, CooperativeStickyAssignor`** | Assignor preference list. **Classic only.** The default *effectively selects `RangeAssignor`, i.e. eager* — see Q4. |
| `group.instance.id` | string | **null** | Static membership (KIP-345). Non-null changes reassignment timing as quoted above. |
| `enable.auto.commit` | boolean | **true** | Must be `false` for transactional / EOS processing. |
| `auto.commit.interval.ms` | int | **5000 (5 s)** | Auto-commit cadence. With auto-commit on, offsets can advance past records you haven't finished. |
| `isolation.level` | string | **`read_uncommitted`** | `read_committed` required to not see aborted/open transactions. **Not the default.** |
| `group.remote.assignor` | string | **null** | Server-side assignor choice. Only under `group.protocol=consumer`. |

### Broker (KIP-848 consumer protocol, and group bounds)

| Setting | Default (4.3) | Governs |
|---|---|---|
| `group.consumer.session.timeout.ms` | **45000 (45 s)** | Session timeout for `consumer`-protocol groups. Replaces the client's `session.timeout.ms`. |
| `group.consumer.heartbeat.interval.ms` | **5000 (5 s)** | Heartbeat cadence for `consumer`-protocol groups. |
| `group.consumer.min.session.timeout.ms` | **45000 (45 s)** | Lower bound. |
| `group.consumer.max.session.timeout.ms` | **60000 (1 min)** | Upper bound. Note: you **cannot** push a consumer-protocol session timeout past 1 minute without changing this. |
| `group.consumer.assignors` | **`uniform,range`** | Server-side assignors; first is default. `uniform` maps to the old `CooperativeStickyAssignor`. |
| `group.min.session.timeout.ms` | **6000 (6 s)** | Classic-protocol lower bound on client `session.timeout.ms`. |
| `group.max.session.timeout.ms` | **1800000 (30 min)** | Classic-protocol upper bound. |
| `transaction.max.timeout.ms` | **900000 (15 min)** | Cap on a producer's transaction timeout. |
| `transactional.id.expiration.ms` | **604800000 (7 days)** | How long a `transactional.id`'s state is retained. |

### Kafka Streams

| Setting | Default (4.3) | Governs |
|---|---|---|
| `num.standby.replicas` | **0** | Shadow copies of **state stores**, to cut failover restore time. Not a processing standby. |
| `processing.guarantee` | **`at_least_once`** | `exactly_once_v2` opts into EOS. **EOS is not on by default.** |
| `acceptable.recovery.lag` | **10000** | Offsets of lag under which an instance counts as caught-up enough to take an active task. |

**A stale-default warning.** Confluent's platform client guide
(docs.confluent.io/platform/current/clients/consumer.html) still says `session.timeout.ms` "default is 10 seconds in
the C/C++ and Java clients." That is out of date for the Java client: KIP-735 raised it from 10 s to 45 s (Kafka 3.0),
motivated by the fact that "transient network/load failures are much more common than genuine client failures." Use
the Apache 4.3 config reference (**45000**) for the Java client. Do not quote the Confluent number.

---

## Rebalance protocols (Q4)

### Eager — and it is still the default

The authoritative definition is the `RebalanceProtocol` javadoc —
https://kafka.apache.org/43/javadoc/org/apache/kafka/clients/consumer/ConsumerPartitionAssignor.RebalanceProtocol.html :

> The rebalance protocol defines partition assignment and revocation semantics. **The purpose is to establish a
> consistent set of rules that all consumers in a group follow in order to transfer ownership of a partition.** ...
> **Failures to follow the rules of the supported protocols would lead to runtime error or undefined behavior.**
>
> The **EAGER** rebalance protocol requires a consumer to **always revoke all its owned partitions** before
> participating in a rebalance event. It therefore allows a complete reshuffling of the assignment.
>
> **COOPERATIVE** rebalance protocol allows a consumer to **retain its currently owned partitions** before
> participating in a rebalance event. The assignor should not reassign any owned partitions immediately, but instead
> may indicate consumers the need for partition revocation so that the revoked partitions can be reassigned to other
> consumers in the next rebalance event.

That first sentence deserves a second read in a locking discussion: exclusivity is a *set of rules that all consumers
follow*, enforced by protocol convention plus commit-time epoch checks — not by a broker-side mutex over the
partition.

**Current default of `partition.assignment.strategy` is eager.** The config reference says so explicitly:

> The default assignor is **[RangeAssignor, CooperativeStickyAssignor], which will use the RangeAssignor by default**,
> but allows upgrading to the CooperativeStickyAssignor with just a single rolling bounce that removes the
> RangeAssignor from the list.

`RangeAssignor` and `RoundRobinAssignor` are eager-only. `StickyAssignor` "follows the eager rebalancing protocol"
(CooperativeStickyAssignor javadoc). So out of the box, on Kafka 4.3, a fresh Java consumer group is **classic
protocol + eager assignment**: every rebalance revokes every partition from every member, group-wide.
`CooperativeStickyAssignor` is opt-in — its javadoc: "To turn on cooperative rebalancing you must set **all** your
consumers to use this PartitionAssignor."

*Secondary source (Confluent official, docs.confluent.io/platform/current/clients/consumer.html)* summarises the
classic protocol's rebalance type as: "Eager or cooperative: assignor dependent. **In some circumstances, all
consumers can pause and revoke all partitions.**"

### KIP-848 (`group.protocol=consumer`)

https://kafka.apache.org/43/operations/consumer-rebalance-protocol/ :

> Starting from Apache Kafka 4.0, the Next Generation of the Consumer Rebalance Protocol (KIP-848) is Generally
> Available (GA), ready for production workloads. It improves the scalability of consumer groups while simplifying
> consumers. It also decreases rebalance times, thanks to its **fully incremental design, which no longer relies on a
> global synchronization barrier.**

> Since Apache Kafka 4.0, the Consumer fully supports the new Consumer rebalance protocol. However, **the protocol is
> not enabled by default. The `group.protocol` configuration must be set to `consumer` to enable it.**

The evolution timeline on that page: 3.7 Early Access, 4.0 GA, **5.0 KafkaConsumer defaults to Consumer protocol**,
6.0 client-side classic support removed. Under `consumer`, these become unusable: `heartbeat.interval.ms`,
`session.timeout.ms`, `partition.assignment.strategy`, `enforceRebalance()`.

KIP-848's reconciliation still preserves revoke-before-assign for cooperating members —
https://cwiki.apache.org/confluence/display/KAFKA/KIP-848 :

> The group coordinator revokes the partitions which are no longer in the target assignment of the member. ... **The
> group coordinator will give the rebalance timeout to the member for the revocation process to complete or kick it
> out from the group otherwise.**
>
> The group coordinator assigns the new partitions to the member ... **while ensuring that partitions which are not
> revoked by other members yet are removed from this set.** In other words, new partitions are incrementally assigned
> to the member when they are revoked by the other members.

> The rebalance timeout is provided by the member when it joins the group. **It is basically the max poll interval
> configured on the client side.**

So KIP-848 does *not* remove the lease — it re-expresses it. The rebalance timeout is still `max.poll.interval.ms`,
and a member that can't revoke inside it is still kicked out.

### Is there a window where ownership is ambiguous or processing stops?

Two different answers, and the framework must not conflate them.

**Processing stops: yes, and how much depends on the protocol.**
- Eager (the default): every rebalance revokes everything, so the whole group stops until the rebalance completes.
- Cooperative: only partitions actually being moved stop, and they stop for two rebalance rounds (revoke, then
  assign). Retained partitions keep processing.
- KIP-848: same incremental shape, driven by the coordinator, no global barrier.

**Ownership is ambiguous: only on the eviction path, and there it genuinely is.** In the graceful path the ordering is
guaranteed — `ConsumerRebalanceListener` javadoc:

> Under normal conditions, if a partition is reassigned from one consumer to another, then **the old consumer will
> always invoke `onPartitionsRevoked` for that partition prior to the new consumer invoking `onPartitionsAssigned`
> for the same partition.**

But when a member is evicted, Apache says the quiet part out loud — same javadoc:

> You can think of revocation as a graceful way to give up ownership of a partition. **In some cases, the consumer may
> not have an opportunity to do so. For example, if the session times out, then the partitions may be reassigned
> before we have a chance to revoke them gracefully.** For this case, we have a third callback
> `onPartitionsLost(Collection)`. The difference between this function and `onPartitionsRevoked(Collection)` is that
> upon invocation of `onPartitionsLost(Collection)`, **the partitions may already be owned by some other members in
> the group** and therefore users would not be able to commit its consumed offsets for example.

"The partitions may already be owned by some other members in the group" is the ambiguity, in Apache's own words. And
under KIP-848: "If the member is fenced by the group coordinator, it will immediately abandon all its partitions and
call `ConsumerRebalanceListener#onPartitionsLost`." Note "immediately" means *when the client next processes a
heartbeat response* — the application thread mid-`process(X)` is still not interrupted.

### What rejects the stale commit, and by what name

**Classic protocol — generation id.** `CommitFailedException` javadoc,
https://kafka.apache.org/43/javadoc/org/apache/kafka/clients/consumer/CommitFailedException.html :

> This exception is raised when an offset commit with `KafkaConsumer.commitSync()` fails with an unrecoverable error.
> **This can happen when a group rebalance completes before the commit could be successfully applied. In this case,
> the commit cannot generally be retried because some of the partitions may have already been assigned to another
> member in the group.**

The wire-level errors are `ILLEGAL_GENERATION` and `UNKNOWN_MEMBER_ID`. KIP-429 specifies the client's reaction: "If
received `UNKNOWN_MEMBER_ID` or `ILLEGAL_GENERATION` from join-group / sync-group / commit / heartbeat response: reset
generation / clear member-id correspondingly, call rebalance listener's `onPartitionsLost` for all the partition and
then re-join group with empty assigned partition."

Every rebalance produces a new generation. *Secondary source (Confluent official, same page)*: "Every rebalance
results in a new **generation** of the group."

**KIP-848 protocol — member epoch.** From KIP-848's `ConsumerGroupHeartbeat` field table:

> **Member Epoch** | int32 | The current epoch of this member. The epoch is the assignment epoch of the assignment
> currently used by this member. **This epoch is the one used to fence the member (e.g. offsets commit).**

> It also ensures that the Member Epoch matches the expected member epoch. If not, the request is rejected with the
> **`FENCED_MEMBER_EPOCH`** error. In this case, the member is expected to immediately give up all its partitions and
> rejoin the group.

*Secondary source (Confluent official, same page)* on the broker-side check:

> When a consumer commits offsets, **the group coordinator validates that the consumer still owns the partitions it is
> committing for. This prevents consumers that have been fenced or whose partitions were reassigned from overwriting
> offsets for partitions they no longer own.** The coordinator uses epoch validation for consumer groups using the
> consumer group protocol (`group.protocol=consumer`). The coordinator tracks an assignment epoch **per partition**
> rather than a single epoch per member.

**The point that must not get lost.** The commit rejection is a *bookkeeping* protection. It prevents the offset from
moving backwards or forwards wrongly. It happens **after** A has finished its work. If A's work was `POST /payments`,
the payment is made. If it was `UPDATE accounts SET balance = balance - 100`, the money is gone. `CommitFailedException`
is a receipt for a side effect you already shipped. Nothing in Kafka rolls it back.

---

## What fencing is available (Q5)

### Producer epoch fencing (KIP-98)

https://cwiki.apache.org/confluence/display/KAFKA/KIP-98 — with a `transactional.id`, Kafka guarantees:

> **Exactly one active producer with a given TransactionalId.** This is achieved by fencing off old generations when a
> new instance with the same TransactionalId comes online.

> We introduce the notion of a **producer epoch**, which enables us to ensure that there is only one legitimate active
> instance of a producer with a given TransactionalId.

`InitPidRequest` is where it happens: "**Bumps up the epoch of the PID**, so that the any previous zombie instance of
the producer is fenced off and cannot move forward with its transaction."

`ProducerFencedException` javadoc:

> This fatal exception indicates that another producer with the same `transactional.id` has been started. **It is only
> possible to have one producer instance with a `transactional.id` at any given time, and the latest one to be started
> "fences" the previous instances so that they can no longer make transactional requests.** When you encounter this
> exception, you must close the producer instance.

**But read the binding condition.** Epoch fencing keys on `transactional.id`, not on the partition. Apache's own
guidance is one producer per *consumer instance* — https://kafka.apache.org/43/design/design/ , "Using Transactions":

> In order to handle transactions properly in combination with rebalancing, **it is advisable to use one producer
> instance for each consumer instance.**

If A and B are different consumer instances with different `transactional.id`s, producer epoch fencing does **nothing
between them** — there is no shared id whose epoch could be bumped. *Secondary source (Confluent official,
confluent.io/blog/transactions-apache-kafka)* is blunt about it:

> **The key to fencing out zombies properly is to ensure that the input topics and partitions in the read-process-write
> cycle is always the same for a given `transactional.id`. If this isn't true, then it is possible for some messages to
> leak through the fencing provided by transactions.**
>
> For instance ... suppose topic-partition tp0 was originally processed by `transactional.id` T0. If, at some point
> later, it could be mapped to another producer with `transactional.id` T1, **there would be no fencing between T0 and
> T1.**

So the classic KIP-98 recipe requires a **static mapping from partition to `transactional.id`** — which is what the
Apache producer javadoc means by "It would typically be derived from the shard identifier in a partitioned, stateful,
application." Get that mapping wrong (or use a random/per-instance id) and you have no zombie fencing at all.

### The mechanism that actually fences a stale consumer-processor: KIP-447

This is the right answer to "how do I fence A?", and its motivating example is verbatim our Q3 scenario —
https://cwiki.apache.org/confluence/display/KAFKA/KIP-447 :

> **Fence Zombie.** A zombie process may invoke `InitProducerId` after falling out of the consumer group. In order to
> distinguish zombie requests, **we need to leverage group coordinator to fence out of sync client.**

> To pass the information to broker, `member.id`, `group.instance.id` and `generation.id` field shall be added to
> `TxnOffsetCommitRequest`, which makes txn offset commit fencing consistent with normal offset fencing.
>
> **If one of the field is not matching correctly on server side, the client will be fenced immediately.** An edge case
> is defined as:
>
> 1. Client A tries to commit offsets for topic partition P1, but haven't got the chance to do txn offset commit
>    before a long GC.
> 2. **Client A gets out of sync and becomes a zombie due to session timeout, group rebalanced.**
> 3. **Another client B was assigned with P1.**
> 4. Client B doesn't see pending offsets because A hasn't committed anything, so it will proceed with potentially
>    `pending` input data
> 5. Client A was back online, and continue trying to do txn commit. **Here if we have `generation.id`, we will catch
>    it!**

Read step 3 and 4 together: Apache's own KIP states, as a designed-for case, that A is a live zombie while B processes
the same input. The fix in step 5 catches A **at commit time**. It does not stop A from doing work in steps 2–4.

Mechanically: call `producer.sendOffsetsToTransaction(offsets, consumer.groupMetadata())`, not the group-id-only
overload. `KafkaProducer` javadoc:

> Thus, the specified `groupMetadata` should be extracted from the used consumer via `KafkaConsumer.groupMetadata()`
> to leverage consumer group metadata. **This will provide stronger fencing than just supplying the `consumerGroupId`
> and passing in `new ConsumerGroupMetadata(consumerGroupId)`.**

KIP-447 also lists the preconditions plainly: "User needs to store transactional offsets inside Kafka group
coordinator, **not in any other external system for the sake of fencing**" and "Producer needs to call
`sendOffsetsToTransaction(offsets, groupMetadata)` to be able to fence properly."

Kafka 4.0 additionally strengthened the protocol — https://kafka.apache.org/43/operations/transaction-protocol/ :
"Transactions Server Side Defense (KIP-890) ... the producer epoch is bumped on every transaction to ensure every
transaction includes the intended messages and duplicates are not written as part of the next transaction." Enabled by
default on 4.0+ brokers via the `transaction.version` feature flag.

### What `read_committed` + EOS actually guarantees

Apache, https://kafka.apache.org/43/design/design/ :

> As a result, Kafka supports exactly-once delivery in Kafka Streams, and the transactional producer and the consumer
> using read-committed isolation level can be used generally to provide exactly-once delivery **when reading,
> processing and writing data on Kafka topics. Exactly-once delivery for other destination systems generally requires
> cooperation with such systems**, but Kafka provides the primitives which makes implementing this feasible.

And on the external case, in the same section:

> When writing to an external system, the limitation is in the need to coordinate the consumer's position with what is
> actually stored as output. The classic way of achieving this would be to introduce a two-phase commit between the
> storage of the consumer position and the storage of the consumers output. **This can be handled more simply and
> generally by letting the consumer store its offset in the same place as its output.**

The required config, verbatim: "The consumer configuration must include `isolation.level=read_committed` and
`enable.auto.commit=false`." Neither is the default — `isolation.level` defaults to `read_uncommitted` and
`enable.auto.commit` defaults to `true`. Kafka Streams' `processing.guarantee` defaults to `at_least_once`.

*Secondary source (Confluent official, developer.confluent.io/learn/kafka-transactions-and-guarantees)*, which states
the limit as directly as anyone does:

> **What Can't Transactions Do?** The main restriction with Transactions is they only work in situations where both the
> input comes from Kafka and the output is written to a Kafka topic. **If you are calling an external service (e.g.,
> via HTTP), updating a database, writing to stdout, or anything other than writing to and from the Kafka broker,
> transactional guarantees won't apply and calls can be duplicated.** ... Put another way, Kafka's transactions are not
> inter-system transactions such as those provided by technologies that implement XA.

### For correctness-grade "only one processor for this key", add one of these

Partitioning gets you serialization and ordering. To get correctness under eviction you must add something. In rough
order of how often it is the right answer:

1. **Make the side effect idempotent, keyed on something stable.** Use the record key plus the partition offset, or a
   business idempotency key, as a dedupe key in the external system. This is the honest, load-bearing fix for
   99% of real handlers, and it is what "at-least-once + idempotent consumer" means. Apache endorses this shape: "In
   many cases messages have a primary key and so the updates are idempotent (receiving the same message twice just
   overwrites a record with another copy of itself)."
2. **Use the offset as a fencing token on your own writes.** You already have a monotonic, per-partition sequence
   number: the record's offset (and `ConsumerRecord.leaderEpoch()`). Store `last_applied_offset` alongside the
   protected row and apply conditionally — `WHERE last_applied_offset < :offset`. This is exactly Kleppmann's fencing
   token, except Kafka hands you the token for free and you don't need a lock service to mint it. It is the single
   cheapest thing in this document and it closes the overlap window for the *state* (not for non-transactional
   effects like emails).
3. **Keep all output in Kafka and use real EOS.** Transactional producer with a partition-stable `transactional.id`,
   `sendOffsetsToTransaction(offsets, consumer.groupMetadata())`, `isolation.level=read_committed`,
   `enable.auto.commit=false`. Then the fencing is genuine and A's zombie transaction is rejected. This is the only
   option on this list that gives you actual exactly-once, and it only applies inside Kafka.
4. **Shrink the window.** Lower `max.poll.records`, keep worst-case batch processing well inside
   `max.poll.interval.ms`, and for unpredictable work take the javadoc's advice: "move message processing to another
   thread, which allows the consumer to continue calling poll while the processor is still working. ... Note also that
   you will need to `pause` the partition so that no new records are received from poll until after thread has
   finished handling those previously returned." This reduces probability, never to zero.
5. **Keep the lock.** If the resource you must serialize is not scoped to the partition — a shared counter across
   keys, a single external file, a third-party API with a per-tenant concurrency limit — or if a second consumer
   group, a REST endpoint, or an operator script can touch the same resource, then partitioning gives you nothing and
   you need a real lock (or a database transaction, which is usually better). No amount of key-partitioning helps
   here.

---

## Verdict for the framework

**"Partition by key" is a legitimate no-lock answer. "Contention is structurally impossible" is not a legitimate way
to say it, and the framework should change that wording.**

What is genuinely true, and is a big deal:

- In the steady state, with a healthy group, there is exactly one consumer per partition. This is a documented
  invariant, not a probabilistic property. You do not need a mutual-exclusion primitive to get single-threaded
  processing per key. That eliminates *all* of the steady-state contention, all of the lock-acquisition latency, all
  of the lock-service dependency, and all of the deadlock and thundering-herd failure modes that come with a lock.
- It is *cheaper* than a lock, not just equivalent: the exclusivity is a side effect of how you already read your
  input, so there is no extra round trip, no extra service to run, and no lock to forget to release.
- The lease renewal is automatic and its default TTL is 5 minutes, so accidental expiry is genuinely rarer than with a
  hand-rolled Redis lock and a 30-second TTL.
- Kafka fences the offset commit for you, on generation id or member epoch, with no code on your part.

What is not true:

- Contention is not *structurally* impossible; it is *architecturally unlikely*, bounded by a lease. The structure that
  makes it "impossible" is `max.poll.interval.ms`, and Apache documents in KIP-62 that the lease is surrendered by a
  background thread while the application thread keeps working. Two processes can execute the same record at the same
  time, and KIP-447 documents this as a case it was designed to catch — at commit time, after the work is done.
- Exclusivity is on **assignment**, not on **processing**. Those are not the same claim and the difference is exactly
  where the bugs live.
- The exclusivity is scoped to one `group.id`. It says nothing about a second consumer group, an API handler, or a
  batch job touching the same entity.
- The exclusivity is anchored to the partition, not the key. Repartitioning breaks the key-to-owner mapping.

**Recommended wording for the framework.** Replace "removes the need for a lock because contention is structurally
impossible" with something closer to:

> Partitioning by key removes the need for a lock in the steady state: within one consumer group, each partition is
> assigned to exactly one consumer, so records for a given key are processed one at a time by one process, with no
> mutual-exclusion primitive. What it does **not** remove is the failure-mode overlap. Partition ownership is a lease
> bounded by `max.poll.interval.ms` (default 5 minutes); a consumer that overruns it loses the partition while still
> processing, and the new owner resumes from the last committed offset. Its stale offset commit is rejected, but its
> side effects are not undone. Partitioning therefore converts a **mutual-exclusion** requirement into an
> **idempotency** requirement — usually a much easier one, and usually satisfied by writing conditionally on the
> record's offset. It is not zero requirement. If the resource you must serialize is not scoped to the partition, or
> is reachable from outside the consumer group, you still need a lock.

That is a strictly better teaching point than "no lock needed", because it tells the reader what to actually do next
(be idempotent, or fence on the offset) instead of letting them believe there is nothing left to do. It also lands the
framework's broader theme: partitioning and Redis-with-a-TTL have the *same* residual failure shape, and the honest
difference is that partitioning's shape is longer-leased, auto-renewed, and comes with a free fencing token for the
one resource Kafka owns.

Suggested talk lines, if useful:

- "Kafka gives you exactly-one-consumer-per-partition. It does not give you exactly-one-*processor*-per-partition.
  Those differ by one config: `max.poll.interval.ms`, default five minutes."
- "`max.poll.interval.ms` is a lock TTL. It's just a lock TTL you didn't know you'd configured, with a much more
  generous default than you'd ever pick for Redis."
- "The consumer that got evicted mid-work finds out when it tries to commit. That's a receipt, not a rollback."
- "KIP-447's motivating example is literally: client A GCs, falls out of the group, B takes the partition and starts
  processing, A comes back and tries to commit. Apache wrote that down. It isn't a corner case somebody on Stack
  Overflow invented."
- "So the question partitioning asks you isn't 'do I need a lock?' — it's 'is my handler idempotent?' That's a better
  question and it's usually a cheaper yes."

---

## Sources

**Apache Kafka 4.3 official documentation** (kafka.apache.org; all pages "Last modified May 22, 2026"):

- https://kafka.apache.org/43/design/design/ — Design. "Consumer Position" (each partition consumed by exactly one
  consumer per group), "Static Membership", "Message Delivery Semantics" (at-most/at-least/exactly-once, external
  systems), "Using Transactions" (the three key aspects; one producer per consumer instance; required configs), "The
  Share Consumer" (explicit contrast: share groups *may* assign a partition to multiple consumers).
- https://kafka.apache.org/43/getting-started/introduction/ — same-key-same-partition and the per-partition ordering
  guarantee.
- https://kafka.apache.org/43/configuration/consumer-configs/ — all consumer defaults quoted in the table, plus the
  verbatim `max.poll.interval.ms` description including the static-membership carve-out, and the
  `partition.assignment.strategy` "will use the RangeAssignor by default" sentence.
- https://kafka.apache.org/43/configuration/broker-configs/ — `group.consumer.*`, `group.min/max.session.timeout.ms`,
  `transaction.max.timeout.ms`, `transactional.id.expiration.ms` defaults.
- https://kafka.apache.org/43/configuration/kafka-streams-configs/ — `num.standby.replicas` = 0,
  `processing.guarantee` = `at_least_once`.
- https://kafka.apache.org/43/streams/developer-guide/config-streams/ — the "shadow copies of local state stores /
  minimize the latency of task failover" description of standbys.
- https://kafka.apache.org/43/streams/architecture/ — standby replicas and caught-up-instance assignment.
- https://kafka.apache.org/43/operations/consumer-rebalance-protocol/ — KIP-848 GA in 4.0, not default,
  server-side assignors (`uniform` default), configs disabled under `consumer`, and the 4.0→6.0 evolution timeline.
- https://kafka.apache.org/43/operations/transaction-protocol/ — KIP-890 server-side defense, epoch bumped every
  transaction, `transaction.version` feature flag.

**Apache Kafka 4.3 javadoc:**

- `.../clients/consumer/KafkaConsumer.html` — "Consumer Groups and Topic Subscriptions" and, critically, "Detecting
  Consumer Failures": the livelock paragraph, "the client will proactively leave the group", and "This is a safety
  mechanism which guarantees that only active members of the group are able to commit offsets."
- `.../clients/consumer/ConsumerRebalanceListener.html` — revoke-before-assign under normal conditions;
  `onPartitionsLost` and "the partitions may already be owned by some other members in the group"; eager vs
  cooperative callback behaviour.
- `.../clients/consumer/ConsumerPartitionAssignor.RebalanceProtocol.html` — the canonical EAGER and COOPERATIVE
  definitions, and "Failures to follow the rules of the supported protocols would lead to runtime error or undefined
  behavior."
- `.../clients/consumer/CooperativeStickyAssignor.html` — StickyAssignor is eager; all consumers must opt in.
- `.../clients/consumer/CommitFailedException.html` — the rejected-commit description.
- `.../common/errors/ProducerFencedException.html` — one producer per `transactional.id`; latest fences previous.
- `.../common/errors/FencedInstanceIdException.html` — duplicate `group.instance.id` fencing.
- `.../clients/producer/KafkaProducer.html` — `transactional.id` purpose ("derived from the shard identifier"),
  `initTransactions()` epoch bump, and `sendOffsetsToTransaction(offsets, groupMetadata)` "stronger fencing".

**KIPs (cwiki.apache.org/confluence/display/KAFKA/...):**

- **KIP-62** — *the* source for Q3. Introduces `max.poll.interval.ms` and the background heartbeat thread; states
  that on expiry "the consumer will stop sending heartbeats and send an explicit LeaveGroup request", and that the
  background thread is what sends it. Also notes the trade: "if they set a lower process timeout, rebalances will
  complete faster, but the risk of commit failures will increase since the consumer can fall out of the group before a
  round of processing completes."
- **KIP-345** — static membership; `group.instance.id`; static members do not send LeaveGroup;
  `FENCED_INSTANCE_ID` / `FencedInstanceIdException`; `RemoveMemberFromGroup`.
- **KIP-429** — cooperative incremental rebalancing; the exactly-one-consumer invariant and why eager revokes
  everything; `ILLEGAL_GENERATION`/`UNKNOWN_MEMBER_ID` → `onPartitionsLost`; `ConsumerGroupMetadata` with
  `generationId()`; "having some partitions to be owned by more than one member" as the hazard.
- **KIP-447** — the zombie-fencing mechanism for consumer-processors. `generation.id`/`member.id`/`group.instance.id`
  on `TxnOffsetCommitRequest`; "If one of the field is not matching correctly on server side, the client will be
  fenced immediately"; and the five-step edge case that is exactly our Q3 timeline.
- **KIP-98** — transactions and idempotence. "Exactly one active producer with a given TransactionalId"; producer
  epoch; `InitPidRequest` bumps the epoch to fence zombies.
- **KIP-848** — next-gen rebalance protocol. Member epoch "is the one used to fence the member (e.g. offsets
  commit)"; `FENCED_MEMBER_EPOCH` / `STALE_MEMBER_EPOCH`; reconciliation with revoke-before-assign; rebalance timeout
  "is basically the max poll interval configured on the client side"; fenced member calls `onPartitionsLost`.
- **KIP-735** — `session.timeout.ms` raised 10 s → 45 s (Kafka 3.0), with the motivation. Use this to reject the
  stale "10 seconds" figure in Confluent's client guide.

**Confluent — official but SECONDARY, and labelled as such throughout:**

- https://docs.confluent.io/platform/current/clients/consumer.html — "Every rebalance results in a new generation";
  the offset-commit ownership validation and per-partition assignment epoch under `group.protocol=consumer`; the
  classic/consumer protocol comparison table ("In some circumstances, all consumers can pause and revoke all
  partitions"); graceful shutdown triggers an immediate rebalance. **Contains a stale default:** it says
  `session.timeout.ms` defaults to 10 s, which is wrong for the Java client since KIP-735.
- https://developer.confluent.io/courses/architecture/consumer-group-protocol/ — the clearest statement of the idle
  extra consumer, and Range-assignor idleness. Jun Rao's transcript on the same page walks the stop-the-world
  rebalance, cooperative sticky, and static membership.
- https://developer.confluent.io/learn/kafka-transactions-and-guarantees/ — "What Can't Transactions Do?": the
  external-side-effect limit, stated more bluntly than Apache states it.
- https://www.confluent.io/blog/transactions-apache-kafka/ — the "zombie instances" framing, and the crucial
  `transactional.id`-must-map-to-fixed-input-partitions warning with the T0/T1 leak example.

---

## Unverified / open

- **The exact overlap duration is not documented anywhere and I did not measure it.** The window is
  `[B begins processing X, A stops processing X]`. Its start is bounded by rebalance latency after A's LeaveGroup
  (protocol- and group-size-dependent). Its end depends entirely on how long A's stuck handler runs — which, by
  construction, is unbounded. Do not put a number on this in the talk. If a number is wanted, it needs a
  demo/measurement, not a citation.
- **Whether A's client library interrupts the application thread on eviction.** KIP-62 says the background thread's
  only jobs are heartbeating and sending LeaveGroup, which strongly implies no interruption, and no doc I found says
  otherwise. But I did not read `ConsumerCoordinator` / `ConsumerNetworkClient` source to confirm there is no path
  that raises into the user thread. If this claim is going to be stated as certain on a slide, read the source or
  write the demo.
- **librdkafka / confluent-kafka-dotnet behaviour.** Everything above is the Java client. KIP-735's rejected-
  alternatives section notes that "librdkafka-based consumers enforce the session timeout **locally**. If the session
  timeout is reached without response from the coordinator, then partitions are automatically revoked and the consumer
  rejoins the group as a new member." That is a materially different local-enforcement model, and .NET services are
  librdkafka-based. I did **not** research librdkafka's `max.poll.interval.ms` handling, its default values, or
  whether it interrupts the consuming thread. If the audience is a .NET shop, this is the most important remaining
  gap.
- **Confluent's `session.timeout.ms` = 10 s claim.** I concluded it is stale for the Java client based on KIP-735 and
  the Apache 4.3 reference (45000). I did not confirm whether it remains accurate for the C/C++ client, which the same
  sentence also covers. Possible it is correct for librdkafka and merely stale for Java.
- **Whether the offset-as-fencing-token pattern is documented by Apache or Confluent as a recommended practice.** I
  inferred it from `ConsumerRecord.leaderEpoch()` being available and from the KafkaProducer javadoc's advice to "add
  the leader epoch as commit metadata", plus Apache's "letting the consumer store its offset in the same place as its
  output". I found no page that presents it as a named pattern. Present it as our recommendation, not as
  Apache's.
- **KIP-1274.** Both the Apache ops page and Confluent reference it for the classic-protocol deprecation timeline
  (5.0 default switch, 6.0 removal). I took the timeline from the Apache 4.3 ops page and did not read KIP-1274
  itself.
- **Broker-side per-partition assignment epoch validation.** Only Confluent's page describes the coordinator tracking
  "an assignment epoch per partition rather than a single epoch per member." I did not find the corresponding KIP or
  Apache page. Verify before quoting that detail as Apache behaviour.
- **Repartitioning as a correctness event.** I reasoned this from the documented facts (keys hash to partitions; the
  group auto-detects new partitions and assigns them) but found no Apache page that spells out the consequence for
  key-level exclusivity. The reasoning is sound; the citation is mine, not theirs.
- **Share groups.** Used here only as evidence-by-contrast for the consumer-group invariant. I did not research their
  own guarantees, their acquisition-lock semantics, or their timeouts — and they are a genuinely interesting
  alternative shape for "work queue with more workers than partitions" that the framework might want covered
  separately.
