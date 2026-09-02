# Locking Glossary

The canonical language for this workspace. Terms are added once the team can
use them correctly, not when they are first introduced.

## Terms

**Invariant**:
A property of the data or the world that must remain true regardless of
concurrency. "This card is charged once" is an invariant.
_Avoid_: rule, constraint (means something specific in SQL), requirement

**Efficiency lock**:
A lock whose failure costs only duplicated work — money, CPU — and never
correctness. A sloppy implementation is acceptable.
_Avoid_: soft lock, best-effort lock

**Correctness lock**:
A lock whose failure corrupts data. An in-process lock, or a row lock inside
the transaction that does the write, is sufficient. An **unfenced lease** is
not, and no lock is once the effect leaves your store.
_Avoid_: hard lock, strict lock

**Reentrant**:
A lock the holding thread can acquire again without deadlocking; it counts
acquisitions and releases only when the count returns to zero.
_Avoid_: recursive lock, nested lock

**Stackable**:
Reentrancy for a Postgres advisory lock — the same session may take it more
than once and must unlock the same number of times.
_Avoid_: nested advisory lock

**Lease**:
A lock that expires on a clock rather than on release. Any system that hands
you work with a timeout and reassigns it if you do not confirm in time.
_Avoid_: TTL lock, timed lock

**Fencing token**:
A monotonically increasing number issued with a lock, which **the protected
resource** checks and uses to reject anything older.
_Avoid_: sequence number. (A version column in `WHERE version = @v` genuinely
is one — optimistic concurrency is fencing applied to a row.)

**Session-scoped**:
Bound to a database connection, released when that connection closes. Correct
on a dedicated connection; leaks on a pooled one.
_Avoid_: connection lock, long lock

**Transaction-scoped**:
Bound to a transaction, released on commit or rollback without exception.
_Avoid_: short lock, scoped lock
