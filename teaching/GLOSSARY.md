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
A lock whose failure corrupts data. Mutual exclusion alone is never sufficient
here; the resource must reject stale writers.
_Avoid_: hard lock, strict lock

**Lease**:
A lock that expires on a clock rather than on release. Any system that hands
you work with a timeout and reassigns it if you do not confirm in time.
_Avoid_: TTL lock, timed lock

**Fencing token**:
A monotonically increasing number issued with a lock, which **the protected
resource** checks and uses to reject anything older.
_Avoid_: sequence number, version (reserve for optimistic concurrency)

**Session-scoped**:
Bound to a database connection, released when that connection closes. Correct
on a dedicated connection; leaks on a pooled one.
_Avoid_: connection lock, long lock

**Transaction-scoped**:
Bound to a transaction, released on commit or rollback without exception.
_Avoid_: short lock, scoped lock
