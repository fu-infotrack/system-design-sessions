# Session 1 is locking; idempotency belongs to session 3

The material drifted toward general concurrency control — constraints,
optimistic concurrency, partitioning — and the user pulled it back, choosing
"keep it Locking" with the alternatives as reference only, zero talk time.

The natural boundary that resolved it: the team's session 3 is *Competing
Consumer & Idempotency*, so idempotency and `FOR UPDATE SKIP LOCKED` have a
home already. Session 1 points at them and does not teach them.

**Implications:** lessons may *name* idempotency as the answer when a side
effect leaves the store — that is unavoidable, it is the correct answer — but
must not teach how to build it. Link forward to session 3 instead.
