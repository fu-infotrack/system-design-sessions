# Working notes

## About the audience

- Mixed seniority, .NET / EF Core / Postgres / Redis / Azure.
- They do **not** run PgBouncer. Do not use it in examples — the EF Core /
  Npgsql pool reproduces the same bug and is their actual stack.
- Marten came up, so event sourcing may be in play somewhere.
- This team corrects its teacher. Lessons must be defensible, not just tidy.

## About the user (the teacher, not the learner)

- Built [LockPlayground](https://github.com/fu-infotrack/LockPlayground) in 2024:
  `lock`, named `Mutex`, `SemaphoreSlim`, with the distributed section left `(TBC)`.
- Pulled scope back to *locking* once already, when it drifted toward general
  concurrency control. Respect that boundary; idempotency belongs to session 3.
- Prefers the answer before the caveat. When a section led with a caveat about
  Kafka partitioning, it read as undermining a correct pattern.

## Unresolved oddity — do not teach

Probing EF Core, a `pg_advisory_lock` followed by `pg_advisory_unlock` through
the **same** `DbContext` (pool max size 1) returned **false** from the unlock,
and the lock was gone afterwards — implying something released it between the
two statements. A direct raw-Npgsql test showed the opposite: no reset happens
on reuse, and the lock survives. The two do not reconcile yet.

Not understood well enough to put in a lesson. Worth a proper investigation if
it comes up, because it touches whether an explicit unlock is reliable at all
under EF Core.

## Open questions

Whether lessons are **pre-work before the talk** or **follow-up after it**.
Asked once, not answered, and not worth blocking on — lessons 1 and 2 are
written to work either way. Ask again if a lesson ever needs to assume the talk
has happened.
