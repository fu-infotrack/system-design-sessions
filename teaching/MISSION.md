# Mission: Locking

## Why

Our team reaches for a distributed lock by reflex when a request looks
concurrent, and usually reaches for the wrong one — a session-scoped Postgres
advisory lock behind EF Core's pool, or a Redis lease guarding a side effect it
cannot fence. The goal is that people **stop asking "which lock?" and start
asking "does this need a lock at all?"**, and can defend the answer in code
review.

This backs the 25-minute session in [`../01-locking/TALK.md`](../01-locking/TALK.md).
The talk is passive and happens once; these lessons are where the decisions
actually stick.

## Success looks like

- Given a scenario, a dev can say **what a double-run costs** and therefore
  whether a sloppy lock is acceptable.
- A dev spots that a side effect leaving our store needs **idempotency, not
  mutual exclusion**, without being prompted.
- Nobody writes `pg_advisory_lock` in a request path again; `pg_advisory_xact_lock`
  inside an explicit transaction becomes the reflex.
- In review, someone asks *"what invariant is this protecting?"* and the answer
  is a property of the data, not "two threads shouldn't run this."
- A dev can name which of our locks are **wall-clock leases** and can therefore
  expire mid-work.

## Constraints

- Lessons must be completable in **under 10 minutes** — this is professional
  development around delivery work, not a course.
- Audience is **mixed seniority**, .NET / EF Core / Postgres / Redis / Azure.
- Must run in a browser with no install. No accounts, no sign-ups.
- Content must survive scrutiny: this team corrects its teacher, which is the
  point. Every claim is cited or demonstrated by a runnable demo.

## Out of scope

- **Idempotency in depth** and `SKIP LOCKED` as a queue — those belong to
  session 3 (Competing Consumer & Idempotency). Lessons point at them; they do
  not teach them.
- Lock-free / wait-free concurrent programming for latency (the HFT sense of
  "no-lock"). Different problem, different mission.
- Anything requiring infrastructure the team does not run — PgBouncer notably.
