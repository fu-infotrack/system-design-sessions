# CLAUDE.md — teaching workspace

This directory is a **teaching workspace** driven by the `teach` skill, not
part of the talk material. Read [`MISSION.md`](MISSION.md) before doing
anything here.

## The one thing to know

**The learner is the team, not the user.** The user is the one teaching them.
They already know this material well — they built the demos, and they have
corrected the source material more than once. Do not write lessons pitched at
them; write lessons pitched at a mixed-seniority .NET team who have not thought
hard about locking.

## Relationship to `../01-locking/`

| | |
|---|---|
| `../01-locking/TALK.md` | the 25-min session. Passive, happens once |
| `../01-locking/FRAMEWORK.md` | the reference artifact the talk teaches |
| `teaching/lessons/` | where the decisions actually stick — retrieval practice |

Lessons draw their content from `../01-locking/FRAMEWORK.md` and cite
`../01-locking/research/` for primary sources. **Do not restate a claim here
that is not already sourced there** — the repo's verification standard applies
(see `../CLAUDE.md`).

## Conventions

- Every lesson links `assets/lesson.css`. Never inline styles a second lesson
  would duplicate.
- Lessons are `lessons/NNNN-dash-case.html`, numbered sequentially.
- Quiz answers must be **the same length** so formatting gives nothing away.
- Under 10 minutes each. Working memory is the binding constraint, not coverage.
- Prefer scenarios from our own stack — EF Core, Npgsql, Service Bus, Azure
  blobs — over generic textbook examples.
