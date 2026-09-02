# Advisory locks in EF Core fail in two different ways, not one

Measured against Postgres 18.3, not inferred:

- `pg_advisory_lock` in a request path **leaks**. The connection returns to the
  pool still holding it, no reset occurs on reuse (verified: 1 lock still held
  after the next connection opens, before it issues any command), and the next
  request's `pg_try_advisory_lock` returns **true** because session locks stack.
- `pg_advisory_xact_lock` **without an explicit transaction is a no-op**. It runs
  in autocommit, so it is acquired and released by the same statement — measured
  as 0 locks held afterwards. No lock ever existed.

**Implications:** teaching "use the xact version" is not sufficient and will
produce the second bug. The rule has two load-bearing halves: the xact function
*and* an explicit `BeginTransactionAsync`. Lesson 2 tests both.
