# Q3 — The Redlock Debate (Kleppmann vs. antirez)

## Summary

Redlock is a client-side distributed lock algorithm designed by Salvatore Sanfilippo (antirez) that acquires a
short-lived, randomly-tokened key on a **majority of N independent Redis masters** and treats the lock as held only
if the majority was won inside the lock's own validity window. In February 2016 Martin Kleppmann published an
analysis arguing Redlock is "neither fish nor fowl" — too heavy for efficiency locks, and unsafe for correctness
locks because it generates no **fencing token** and because its safety rests on timing assumptions (bounded clock
drift, bounded network delay, bounded process pauses) that real systems violate. antirez replied the next day,
accepting the point about monotonic clocks, rejecting the fencing-token argument as assuming a linearizable
resource that most real workloads do not have, and disputing the network-delay critique on the grounds that
Redlock re-checks elapsed time *after* acquisition. Crucially the two men **agree** on the central fact: once a
TTL-based lock's validity window elapses, mutual exclusion is gone, and this is true of every auto-releasing
distributed lock, not just Redlock. What they disagree about is what you are supposed to do about it. The redis.io
documentation today links both posts and has absorbed part of Kleppmann's critique into its own "Disclaimer about
consistency" section.

---

## Findings

### 1. The Redlock algorithm, as officially specified

**The three properties Redlock claims** — https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/

> 1. Safety property: Mutual exclusion. At any given moment, only one client can hold a lock.
> 2. Liveness property A: Deadlock free. Eventually it is always possible to acquire a lock, even if the client that locked a resource crashes or gets partitioned.
> 3. Liveness property B: Fault tolerance. As long as the majority of Redis nodes are up, clients are able to acquire and release locks.

**The setup: N independent masters, no replication between them** — same URL

> In the distributed version of the algorithm we assume we have N Redis masters. Those nodes are totally
> independent, so we don't use replication or any other implicit coordination system. [...] In our examples we set
> N=5, which is a reasonable value, so we need to run 5 Redis masters on different computers or virtual machines in
> order to ensure that they'll fail in a mostly independent way.

**The five acquisition steps, verbatim** — same URL

> 1. It gets the current time in milliseconds.
> 2. It tries to acquire the lock in all the N instances in parallel, using the same key name and random value in all the instances. During step 2, when setting the lock in each instance, the client uses a timeout which is small compared to the total lock auto-release time in order to acquire it. For example if the auto-release time is 10 seconds, the timeout could be in the ~ 5-50 milliseconds range. This prevents the client from staying blocked too long when communicating with an unavailable Redis node, ensuring the connection attempt times out quickly.
> 3. The client computes how much time elapsed in order to acquire the lock, by subtracting from the current time the timestamp obtained in step 1. If and only if the client was able to acquire the lock in the majority of the instances (at least 3), and the total time elapsed to acquire the lock is less than lock validity time, the lock is considered to be acquired.
> 4. If the lock was acquired, its validity time is considered to be the initial validity time minus the time elapsed, as computed in step 3.
> 5. If the client failed to acquire the lock for some reason (either it was not able to lock N/2+1 instances or the validity time is negative), it will try to unlock all the instances (even the instances it believed it was not able to lock).

**Clock-drift factor and the validity computation** — same URL

> But if the first key was set at worst at time T1 (the time we sample before contacting the first server) and the
> last key was set at worst at time T2 (the time we obtained the reply from the last server), we are sure that the
> first key to expire in the set will exist for at least `MIN_VALIDITY=TTL-(T2-T1)-CLOCK_DRIFT`. All the other keys
> will expire later, so we are sure that the keys will be simultaneously set for at least this time.

**The timing assumption stated by the algorithm itself** — same URL, "Is the Algorithm Asynchronous?"

> The algorithm relies on the assumption that while there is no synchronized clock across the processes, the local
> time in every process updates at approximately at the same rate, with a small margin of error compared to the
> auto-release time of the lock. [...] At this point we need to better specify our mutual exclusion rule: it is
> guaranteed only as long as the client holding the lock terminates its work within the lock validity time (as
> obtained in step 3), minus some time (just a few milliseconds in order to compensate for clock drift between
> processes).

**Crash recovery / fsync / delayed restart** — same URL

> Basically to see the problem here, let's assume we configure Redis without persistence at all. A client acquires
> the lock in 3 of 5 instances. One of the instances where the client was able to acquire the lock is restarted, at
> this point there are again 3 instances that we can lock for the same resource, and another client can lock it
> again, violating the safety property of exclusivity of lock.

> In theory, if we want to guarantee the lock safety in the face of any kind of instance restart, we need to enable
> `fsync=always` in the persistence settings. This will affect performance due to the additional sync overhead.

> To guarantee this we just need to make an instance, after a crash, unavailable for at least a bit more than the
> max `TTL` we use. [...] Using *delayed restarts* it is basically possible to achieve safety even without any kind
> of Redis persistence available, however note that this may translate into an availability penalty.

**redis.io's own current disclaimer** (post-debate; the docs have absorbed part of the critique) — same URL

> 1. You should implement fencing tokens. This is especially important for processes that can take significant time
>    and applies to any distributed locking system. Extending locks' lifetime is also an option, but don't assume
>    that a lock is retained as long as the process that had acquired it is alive.
> 2. Redis is not using monotonic clock for TTL expiration mechanism. That means that a wall-clock shift may result
>    in a lock being acquired by more than one process. Even though the problem can be mitigated by preventing
>    admins from manually setting the server's time and setting up NTP properly, there's still a chance of this
>    issue occurring in real life and compromising consistency.

The page also carries an "Analysis of Redlock" section that links **both** posts:

> 1. Martin Kleppmann analyzed Redlock here. A counterpoint to this analysis can be found here.

---

### 2. Kleppmann's argument, precisely

Source throughout: https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html

**(a) The efficiency vs. correctness distinction.** He frames the whole post around asking "what would happen if the
lock failed?"

> **Efficiency**: Taking a lock saves you from unnecessarily doing the same work twice (e.g. some expensive
> computation). If the lock fails and two nodes end up doing the same piece of work, the result is a minor increase
> in cost (you end up paying 5 cents more to AWS than you otherwise would have) or a minor inconvenience (e.g. a
> user ends up getting the same email notification twice).

> **Correctness**: Taking a lock prevents concurrent processes from stepping on each others' toes and messing up the
> state of your system. If the lock fails and two nodes concurrently work on the same piece of data, the result is a
> corrupted file, data loss, permanent inconsistency, the wrong dose of a drug administered to a patient, or some
> other serious problem.

> Both are valid cases for wanting a lock, but you need to be very clear about which one of the two you are dealing with.

**(b) The GC-pause / process-pause timeline.** The diagram shows Client 1 acquiring the lease, being paused past the
lease expiry, Client 2 acquiring the lease, and then Client 1 waking up and writing anyway.

> In this example, the client that acquired the lock is paused for an extended period of time while holding the lock
> – for example because the garbage collector (GC) kicked in. The lock has a timeout (i.e. it is a lease), which is
> always a good idea (otherwise a crashed client could end up holding a lock forever and never releasing it).
> However, if the GC pause lasts longer than the lease expiry period, and the client doesn't realise that it has
> expired, it may go ahead and make some unsafe change.

He forecloses the obvious "just check before writing" fix:

> You cannot fix this problem by inserting a check on the lock expiry just before writing back to storage. Remember
> that GC can pause a running thread at *any point*, including the point that is maximally inconvenient for you
> (between the last check and the write operation).

And extends it from pauses to the network:

> Packet networks such as Ethernet and IP may delay packets *arbitrarily*, and they do: in a famous incident at
> GitHub, packets were delayed in the network for approximately 90 seconds. This means that an application process
> may send a write request, and it may reach the storage server a minute later when the lease has already expired.

> Even in well-managed networks, this kind of thing can happen. You simply cannot make any assumptions about timing,
> which is why the code above is fundamentally unsafe, no matter what lock service you use.

Note the scope of that last sentence: **no matter what lock service you use** — this is the point of agreement.

**(c) Fencing tokens.** His definition and the mechanism:

> The fix for this problem is actually pretty simple: you need to include a fencing token with every write request
> to the storage service. In this context, a fencing token is simply a number that increases (e.g. incremented by
> the lock service) every time a client acquires the lock.

> Client 1 acquires the lease and gets a token of 33, but then it goes into a long pause and the lease expires.
> Client 2 acquires the lease, gets a token of 34 (the number always increases), and then sends its write to the
> storage service, including the token of 34. Later, client 1 comes back to life and sends its write to the storage
> service, including its token value 33. However, the storage server remembers that it has already processed a write
> with a higher token number (34), and so it rejects the request with token 33.

> Note this requires the storage server to take an active role in checking tokens, and rejecting any writes on which
> the token has gone backwards. But this is not particularly hard, once you know the trick. And provided that the
> lock service generates strictly monotonically increasing tokens, this makes the lock safe. For example, if you are
> using ZooKeeper as lock service, you can use the zxid or the znode version number as fencing token, and you're in
> good shape.

And the charge against Redlock:

> However, this leads us to the first big problem with Redlock: *it does not have any facility for generating fencing
> tokens*. The algorithm does not produce any number that is guaranteed to increase every time a client acquires a
> lock. This means that even if the algorithm were otherwise perfect, it would not be safe to use, because you cannot
> prevent the race condition between clients in the case where one client is paused or its packets are delayed.

> And it's not obvious to me how one would change the Redlock algorithm to start generating fencing tokens. The
> unique random value it uses does not provide the required monotonicity. Simply keeping a counter on one Redis node
> would not be sufficient, because that node may fail. Keeping counters on several nodes would mean they would go out
> of sync. It's likely that you would need a consensus algorithm just to generate the fencing tokens.

**(d) The system-model critique.** He contrasts Redlock with the standard academic model:

> In the academic literature, the most practical system model for this kind of algorithm is the *asynchronous model
> with unreliable failure detectors*. In plain English, this means that the algorithms make no assumptions about
> timing: processes may pause for arbitrary lengths of time, packets may be arbitrarily delayed in the network, and
> clocks may be arbitrarily wrong – and the algorithm is nevertheless expected to do the right thing.

> For algorithms in the asynchronous model this is not a big problem: these algorithms generally ensure that their
> *safety* properties always hold, without making any timing assumptions. Only liveness properties depend on timeouts
> or some other failure detector. [...] the performance of an algorithm might go to hell, but the algorithm will
> never make an incorrect decision.

> However, Redlock is not like this. Its safety depends on a lot of timing assumptions: it assumes that all Redis
> nodes hold keys for approximately the right length of time before expiring; that the network delay is small
> compared to the expiry duration; and that process pauses are much shorter than the expiry duration.

On clocks specifically:

> Note that Redis uses `gettimeofday`, not a monotonic clock, to determine the expiry of keys. The man page for
> `gettimeofday` explicitly says that the time it returns is subject to discontinuous jumps in system time – that is,
> it might suddenly jump forwards by a few minutes, or even jump back in time (e.g. if the clock is stepped by NTP
> because it differs from a NTP server by too much, or if the clock is manually adjusted by an administrator).

His two concrete break scenarios:

> **[Clock jump]** Client 1 acquires lock on nodes A, B, C. Due to a network issue, D and E cannot be reached. The
> clock on node C jumps forward, causing the lock to expire. Client 2 acquires lock on nodes C, D, E. Due to a
> network issue, A and B cannot be reached. Clients 1 and 2 now both believe they hold the lock.

> **[Process pause]** Client 1 requests lock on nodes A, B, C, D, E. While the responses to client 1 are in flight,
> client 1 goes into stop-the-world GC. Locks expire on all Redis nodes. Client 2 acquires lock on nodes A, B, C, D,
> E. Client 1 finishes GC, and receives the responses from Redis nodes indicating that it successfully acquired the
> lock (they were held in client 1's kernel network buffers while the process was paused). Clients 1 and 2 now both
> believe they hold the lock.

He then names the three assumptions explicitly:

> These examples show that Redlock works correctly only if you assume a synchronous system model – that is, a system
> with the following properties: bounded network delay [...], bounded process pauses [...], and bounded clock error [...]

> As soon as those timing assumptions are broken, Redlock may violate its safety properties, e.g. granting a lease to
> one client before another has expired. If you're depending on your lock for correctness, "most of the time" is not
> enough – you need it to always be correct.

**(e) fsync / durability and the crashed-restarted node.**

> A similar issue could happen if C crashes before persisting the lock to disk, and immediately restarts. For this
> reason, the Redlock documentation recommends delaying restarts of crashed nodes for at least the time-to-live of
> the longest-lived lock. But this restart delay again relies on a reasonably accurate measurement of time, and would
> fail if the clock jumps.

**(f) His conclusion and recommendation.**

> I think the Redlock algorithm is a poor choice because it is "neither fish nor fowl": it is unnecessarily
> heavyweight and expensive for efficiency-optimization locks, but it is not sufficiently safe for situations in
> which correctness depends on the lock.

> If you need locks only on a best-effort basis (as an efficiency optimization, not for correctness), I would
> recommend sticking with the straightforward single-node locking algorithm for Redis (conditional set-if-not-exists
> to obtain a lock, atomic delete-if-value-matches to release a lock), and documenting very clearly in your code that
> the locks are only approximate and may occasionally fail. Don't bother with setting up a cluster of five Redis nodes.

> On the other hand, if you need locks for correctness, please don't use Redlock. Instead, please use a proper
> consensus system such as ZooKeeper, probably via one of the Curator recipes that implements a lock. (At the very
> least, use a database with reasonable transactional guarantees.) And please enforce use of fencing tokens on all
> resource accesses under the lock.

He is explicitly not attacking Redis generally:

> Before I go into the details of Redlock, let me say that I quite like Redis, and I have successfully used it in
> production in the past. [...] None of the above diminishes the usefulness of Redis for its intended purposes.
> Salvatore has been very dedicated to the project for years, and its success is well deserved.

---

### 3. antirez's rebuttal, precisely

Source throughout: https://antirez.com/news/101 ("Is Redlock safe?", published the day after Kleppmann's post)

**(a) Framing.** He first restates what Redlock is and why he wrote it:

> Redlock is a client side distributed locking algorithm I designed to be used with Redis, but the algorithm
> orchestrates, client side, a set of nodes that implement a data store with certain capabilities, in order to create
> a multi-master fault tolerant, and hopefully safe, distributed lock with auto release capabilities. You can
> implement Redlock using MySQL instead of Redis, for example.

> The algorithm's goal was to move away people that were using a single Redis instance, or a master-slave setup with
> failover, in order to implement distributed locks, to something much more reliable and safe, but having a very low
> complexity and good performance.

He welcomes the analysis:

> It is great that Martin published an analysis, I asked for an analysis in the original Redlock specification here
> [...] So thank you Martin. However I don't agree with the analysis.

**(b) On auto-release — this is where he concedes Kleppmann's core factual premise.**

> A distributed lock without an auto release mechanism, where the lock owner will hold it indefinitely, is basically
> useless. [...] So practical locks are provided to clients with a maximum time to live. **After the expire time, the
> mutual exclusion guarantee, which is the *main* property of the lock, is gone: another client may already have the
> lock.**

**(c) On fencing tokens.** He raises five objections. Verbatim:

> 1. Most of the times when you need a distributed lock system that can guarantee mutual exclusivity, when this
>    property is violated you already lost. Distributed locks are very useful exactly when we have no other control in
>    the shared resource. In his analysis, Martin assumes that you always have some other way to avoid race conditions
>    when the mutual exclusivity of the lock is violated. I think this is a very strange way to reason about distributed
>    locks with strong guarantees, it is not clear why you would use a lock with strong properties at all if you can
>    resolve races in a different way.

> 2. If your data store can always accept the write only if your token is greater than all the past tokens, than it's
>    a linearizable store. If you have a linearizable store, you can just generate an incremental ID for each Redlock
>    acquired, so this would make Redlock equivalent to another distributed lock system that provides an incremental
>    token ID with every new lock. However in the next point I'll show how this is not needed.

> 3. However "2" is not a sensible choice anyway: most of the times the result of working to a shared resource is not
>    writing to a linearizable store, so what to do? Each Redlock is associated with a large random token (which is
>    generated in a way that collisions can be ignored. The Redlock specification assumes textually "20 bytes from
>    /dev/urandom"). What do you do with a unique token? For example you can implement Check and Set. When starting to
>    work with a shared resource, we set its state to "`<token>`", then we operate the read-modify-write only if the
>    token is still the same when we write.

> 4. Note that in certain use cases, one could say, it's useful anyway to have ordered tokens. While it's hard to
>    think at an use case, note that for the same GC pauses Martin mentions, the order in which the token was acquired,
>    does not necessarily respects the order in which the clients will attempt to work on the shared resource, so the
>    lock order may not be casually related to the effects of working to a shared resource.

> 5. Most of the times, locks are used to access resources that are updated in a way that is non transactional.
>    Sometimes we use distributed locks to move physical objects, for example. Or to interact with another external API,
>    and so forth.

His bottom line on fencing:

> However even if you happen to agree with Martin about the fact the above is very useful, the bottom line is that a
> unique identifier for each lock can be used for the same goals, but is much more practical in terms of not
> requiring strong guarantees from the store.

And he notes the fencing critique is not really about Redlock at all:

> The above criticism is basically common to everything which is a distributed lock with auto release, not providing
> a monotonically increasing counter with each lock.

**(d) On clock drift — the "local, relative" rebuttal.**

> Redlock assumes a semi synchronous system model where different processes can count time at more or less the same
> "speed". The different processes don't need in any way to have a bound error in the absolute time. What they need
> to do is just, for example, to be able to count 5 seconds with a maximum of 10% error. So one counts actual 4.5
> seconds, another 5.5 seconds, and we are fine.

On Kleppmann's two clock-jump causes (admin sets the clock; ntpd steps the clock):

> The above two problems can be avoided by "1" not doing this (otherwise even corrupting a Raft log with "echo foo >
> /my/raft/log.bin" is a problem), and "2" using an ntpd that does not change the time by jumping directly, but by
> distributing the change over the course of a larger time span.

The concession:

> However I think Martin is right that Redis and Redlock implementations should switch to the monotonic time API
> provided by most operating systems in order to make the above issues less of a problem. This was proposed several
> times in the past, adds a bit of complexity inside Redis, but is a good idea: I'll implement this in the next weeks.

And the comparison to GPS-based semi-synchronous designs:

> Note that there are past attempts to implement distributed systems even assuming a bound absolute time error (by
> using GPS units). Redlock does not require anything like that, just the ability of different processes to count 10
> seconds as 9.5 or 11.2 (+/- 2 seconds max in the example) [...] The Redlock system model does not have these
> complexities nor requires additional hardware, just the computer clock, and even a very cheap clock with all the
> obvious biases due to the crystal temperature and other things influencing the precision.

> Can a process count relative time with a fixed percentage of maximum error? I think this is a sounding YES, and is
> simpler to reply yes to this than to: "can a process write a log without corrupting it"?

**(e) On bounded network delay — he denies the assumption is required.**

> Martin also states that Redlock requires bound messages maximum delays, which is not correct as far as I can tell

> Note steps 1 and 3. Whatever delay happens in the network or in the processes involved, after acquiring the
> majority we *check again* that we are not out of time. The delay can only happen after steps 3, resulting into the
> lock to be considered ok while actually expired, that is, we are back at the first problem Martin identified of
> distributed locks where the client fails to stop working to the shared resource before the lock validity expires.
> **Let me tell again how this problem is common with *all the distributed locks implementations*, and how the token as
> a solution is both unrealistic and can be used with Redlock as well.**

> Note that whatever happens between 1 and 3, you can add the network delays you want, the lock will always be
> considered not valid if too much time elapsed, so Redlock looks completely immune from messages that have unbound
> delays between processes. It was designed with this goal in mind, and I cannot see how the above race condition
> could happen.

He explicitly invites correction here:

> Yet Martin's blog post was also reviewed by multiple DS experts, so I'm not sure if I'm missing something here or
> simply the way Redlock works was overlooked simultaneously by many. I'll be happy to receive some clarification
> about this.

On process pauses specifically:

> The above also addresses "process pauses" concern number 3. Pauses during the process of acquiring the lock don't
> have effects on the algorithm's correctness. They can however, affect the ability of a client to make work within
> the specified lock time to live, as with any other distributed lock with auto release, as already covered above.

And he generalises the "check the clock before and after" advice to every lock system:

> In server-side implementations of a distributed lock with auto-release, the client may ask to acquire a lock, the
> server may allow the client to do so, but the process can stop into a GC pause or the network may be slow or
> whatever, so the client may receive the "OK, the lock is your" too late, when the lock is already expired. However
> you can do a lot to avoid your process sleeping for a long time, and you can't do much to avoid network delays, so
> the steps to check the time before/after the lock is acquired, to see how much time is left, should actually be
> common practice even when using other systems implementing locks with an expiry.

**(f) On fsync / crash durability — delayed restart is presented as an option, not a requirement.**

> At some point Martin talks about the fact that Redlock uses delayed restarts of nodes. This requires, again, the
> ability to be able to wait more or less a specified amount of time, as covered above. Useless to repeat the same
> things again. However what is important about this is that, this step is optional. You could configure each Redis
> node to fsync at every operation, so that when the client receives the reply, it knows the lock was already
> persisted on disk. This is how most other systems providing strong guarantees work. The very interesting thing
> about Redlock is that you can opt-out any disk involvement at all by implementing delayed restarts. This means it's
> possible to process hundreds of thousands locks per second with a few Redis instances, which is something
> impossible to obtain with other systems.

**(g) His conclusion.**

> I think Martin has a point about the monotonic API, Redis and Redlock implementations should use it to avoid issues
> due to the system clock being altered. However I can't identify other points of the analysis affecting Redlock
> safety, as explained above, nor do I find his final conclusions that people should not use Redlock when the mutual
> exclusion guarantee is needed, justified.

> It would be great to both receive more feedbacks from experts and to test the algorithm with Jepsen, or similar
> tools, to accumulate more data.

---

### 4. Where they actually AGREE

This is the part usually lost in third-party retellings. On the record, both authors state:

| Point | Kleppmann | antirez |
|---|---|---|
| A lock with a TTL loses mutual exclusion once the TTL elapses | "if the GC pause lasts longer than the lease expiry period, and the client doesn't realise that it has expired, it may go ahead and make some unsafe change" | "After the expire time, the mutual exclusion guarantee, which is the *main* property of the lock, is gone: another client may already have the lock." |
| A TTL is nonetheless necessary | "The lock has a timeout (i.e. it is a lease), which is always a good idea (otherwise a crashed client could end up holding a lock forever and never releasing it)." | "A distributed lock without an auto release mechanism, where the lock owner will hold it indefinitely, is basically useless." |
| The overrun problem is not specific to Redlock | "You simply cannot make any assumptions about timing, which is why the code above is fundamentally unsafe, **no matter what lock service you use**." / "any system in which the clients may experience a GC pause has this problem" | "this problem is common with ***all the distributed locks implementations***" |
| Redis should use a monotonic clock | "Note that Redis uses `gettimeofday`, not a monotonic clock, to determine the expiry of keys." | "I think Martin has a point about the monotonic API, Redis and Redlock implementations should use it" |
| The single-instance `SET NX PX` + compare-and-delete lock is the right shape for a best-effort lock | recommends exactly it for efficiency locks | specifies exactly it as the per-instance primitive, and wrote Redlock to move people off *replicated/failover* single locks |

So the disagreement is narrower than it is usually presented. It reduces to three genuine forks:

1. **Is a monotonic fencing token the right remedy?** Kleppmann: yes, and the resource must enforce it. antirez: a
   *unique* token plus check-and-set achieves the same end without demanding a linearizable resource, and demanding
   a linearizable resource assumes away the problem the lock exists to solve.
2. **Does Redlock require bounded network delay for safety?** Kleppmann: yes. antirez: no — steps 1 and 3 re-measure
   elapsed time after the majority is won, so delay can only shrink the validity window, never falsely widen it.
   (antirez himself asks for clarification on this point; the two did not converge publicly.)
3. **Is "bounded local clock drift" a reasonable safety assumption?** Kleppmann: no, safety must not depend on any
   timing assumption. antirez: yes, counting relative time to within a fixed percentage is at least as reliable as
   assuming a disk write is not corrupted.

Note also that Kleppmann's process-pause scenario and antirez's steps-1-and-3 rebuttal are arguing about **slightly
different windows**. Kleppmann's scenario has the pause happen *while responses are in flight* (before step 3);
antirez says step 3's re-check catches exactly that. What antirez concedes is the window *after* step 3 — which is
the same window both agree no TTL lock can protect.

---

## Talk-ready points

- "Redlock is five independent Redis masters — no replication between them. You `SET` the same key with the same
  random value on all five, and you own the lock only if you got a majority **and** the whole round trip finished
  inside the lock's own TTL. Your remaining validity is TTL minus how long acquisition took, minus a clock-drift
  fudge factor: `MIN_VALIDITY = TTL - (T2 - T1) - CLOCK_DRIFT`."
- "Kleppmann's first question is the one worth stealing: *what happens if the lock fails?* If the answer is 'we pay
  AWS five cents twice', that's an efficiency lock. If the answer is 'a patient gets the wrong dose', that's a
  correctness lock. Different answers, different tools."
- "His killer diagram: Client 1 takes a 30-second lease, the GC stops the world for 40 seconds, the lease expires,
  Client 2 takes the lease and writes, then Client 1 wakes up and writes on top of it. And you cannot fix that by
  checking the clock just before the write — GC can pause you *between* the check and the write."
- "A fencing token is a monotonically increasing number handed out with the lock. Client 1 got 33, Client 2 got 34.
  The storage service remembers it has seen 34 and rejects the write carrying 33. The load-bearing word there is
  *the storage service* — the resource itself has to check. That's the whole idea and it's also the whole objection."
- "Kleppmann's charge is not that Redlock has a bug. It's that Redlock's *safety* depends on timing — bounded network
  delay, bounded process pauses, bounded clock error — and safety properties are not supposed to depend on timing.
  Only liveness is."
- "antirez's sharpest counter is not about clocks. It's this: if your resource can reject a stale fencing token, your
  resource is already a linearizable store — and if you had one of those, why did you need a strong distributed lock
  in the first place? Most real resources are 'send this email', 'call this API', 'move this physical object'. You
  cannot fence those."
- "antirez's other counter is that Redlock re-checks the clock *after* winning the majority — steps 1 and 3 — so an
  arbitrarily slow network can only shrink your validity window, never fake one. He explicitly asked for
  clarification on this and, publicly, never got a rebuttal."
- "On persistence: without `fsync=always`, a Redis node that crashes and restarts forgets it granted a lock, so 3 of
  5 becomes lockable again and two clients hold the lock. antirez's answer is *delayed restart* — keep a crashed node
  out of the pool for longer than the max TTL. Kleppmann's response is that this too depends on measuring time correctly."
- "Here is the thing almost everyone gets wrong about this debate: they agree on the central fact. Both say — in
  print — that once the TTL elapses, mutual exclusion is gone, and that this is true of **every** auto-releasing
  distributed lock, ZooKeeper included. antirez: 'this problem is common with all the distributed locks
  implementations.' Kleppmann: 'no matter what lock service you use.'"
- "And redis.io settled part of it in the docs. The official page now says, in its own voice: 'You should implement
  fencing tokens... applies to any distributed locking system' and 'Redis is not using monotonic clock for TTL
  expiration mechanism.' The docs link both blog posts."
- Practical takeaway for our own code: "Decide efficiency or correctness first. If efficiency — one Redis instance,
  `SET NX PX`, compare-and-delete, and a comment in the code saying the lock is approximate. If correctness — the
  lock alone is never enough; you need the *resource* to reject stale writers, whether that's a fencing token, a
  compare-and-set on a version column, or a lease the storage service itself enforces."

### How to present this fairly

- **Do not stage it as a winner-takes-all.** Neither post is a knockout and neither author claimed one. Kleppmann
  opens by saying he likes Redis and praising antirez; antirez opens by thanking Kleppmann for the analysis he had
  explicitly asked for. Reproduce that tone.
- **Lead with the agreement, not the disagreement.** Put the "a TTL lock cannot guarantee correctness by itself"
  agreement on a slide *before* you show the disputed points. It reframes the rest as a narrow technical
  disagreement between two people who share the same premise, which is what it is.
- **Quote both, at similar length.** If you quote Kleppmann's fencing-token paragraph, quote antirez's linearizable-
  store reply next to it. Quoting one and paraphrasing the other is where most retellings go wrong.
- **Separate the three disputes.** Fencing tokens, network delay, clock drift. They have different strengths.
  antirez's fencing rebuttal is a genuine design argument; his network-delay rebuttal is a specific technical claim
  he asked to have checked; his clock-drift position he partially conceded (monotonic clocks).
- **Flag the unresolved bit honestly.** antirez asked publicly whether he was missing something on steps 1 and 3.
  Say so, and say that as far as these two primary sources go, it was not publicly resolved between them.
- **Do not attribute conclusions to redis.io that it does not make.** The docs link both posts and say "you should
  implement fencing tokens" — they do not say Redlock is unsafe, and they do not say Kleppmann was wrong.

---

## Sources

**Primary**

- https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html — Martin Kleppmann, "How to do
  distributed locking", 8 Feb 2016. The original critique. Diagrams are from *Designing Data-Intensive Applications*
  chapters 8-9.
- https://antirez.com/news/101 — Salvatore Sanfilippo (antirez), "Is Redlock safe?", published the day after
  Kleppmann's post. The original reply from Redis's author and Redlock's designer.
- https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/ — redis.io, "Distributed Locks with
  Redis". The canonical Redlock specification: safety/liveness properties, the five acquisition steps, MIN_VALIDITY,
  fsync/delayed-restart discussion, the "Disclaimer about consistency" section, and the "Analysis of Redlock"
  section linking both posts.
- https://redis.io/docs/latest/develop/clients/patterns/distributed-locks/index.html.md — machine-readable Markdown
  rendering of the same page (useful for exact quoting).

**Referenced within the primary sources (not independently fetched)**

- Gray & Cheriton, "Leases: an efficient fault-tolerant mechanism for distributed file cache consistency" —
  http://dl.acm.org/citation.cfm?id=74870 — cited by redis.io as the precedent for bounded-clock-drift lease designs.
- Chandra & Toueg, "Unreliable failure detectors for reliable distributed systems" — cited by Kleppmann as the
  standard system model.
- The GitHub 90-second packet delay incident — cited by Kleppmann as evidence against bounded network delay.

---

## Unverified / open

- **Whether Kleppmann ever publicly answered antirez's steps-1-and-3 network-delay rebuttal.** antirez explicitly
  asked ("I'm not sure if I'm missing something here"). I did not find a follow-up from Kleppmann in these two
  primary sources. Do not assert either way from this research.
- **Whether Redis actually switched to a monotonic clock for TTL expiry.** antirez wrote in Feb 2016 "I'll implement
  this in the next weeks." The current redis.io distributed-locks page still states "Redis is not using monotonic
  clock for TTL expiration mechanism" — but I have not verified against the Redis source what the expiry path uses
  today, and the docs sentence may simply be stale. Flag as unconfirmed if it comes up.
- **Whether Redlock was ever put through a Jepsen test**, as antirez requested. Not checked; no primary source found
  in this pass.
- **The exact publication date of antirez's post.** The page renders a relative timestamp ("3850 days ago" at time of
  fetch) rather than an absolute date. It is the day after Kleppmann's 8 Feb 2016 post by his own description
  ("yesterday published"), so 9 Feb 2016 — but the page itself does not print that date.
- **`CLOCK_DRIFT` is not given a concrete value or derivation by redis.io** beyond "just a few milliseconds in order
  to compensate for clock drift between processes". Implementations pick their own factor; do not quote a specific
  number as official.
