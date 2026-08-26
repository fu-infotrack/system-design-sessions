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

## Open question

Whether lessons are **pre-work before the talk** or **follow-up after it**.
This changes the zone of proximal development significantly and is not yet
answered. Lesson 0001 is deliberately written to work either way.
