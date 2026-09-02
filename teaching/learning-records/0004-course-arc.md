# The six-lesson arc, and why it is ordered this way

The course covers every MISSION success criterion once:

| Lesson | Criterion |
|---|---|
| 1 Efficiency or correctness | can say what a double-run costs |
| 2 The lock that was never there | nobody writes `pg_advisory_lock` in a request path |
| 3 Name the invariant | asks "what invariant is this protecting?" in review |
| 4 When a lock cannot help | spots that an external side effect needs idempotency |
| 5 Every lease expires | can name which of our locks are wall-clock leases |
| 6 Walking the tree | capstone — retrieval practice across all five |

**The ordering is deliberate and worth preserving.** Lessons 1 and 2 are
concrete wins that need no prior grounding. Lesson 3 is the most abstract, and
would have been a weak opener — it lands better once someone has already been
burned by 1 and 2. Lessons 4 and 5 are the two limits of locking. Only then the
synthesis.

**Implications:** if a lesson 7 is ever added, it belongs *after* the capstone
as an elective, not spliced into the arc. Renumbering would break the nav links
and the learning records that reference lesson numbers.
