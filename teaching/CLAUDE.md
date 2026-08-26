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

## Viewing lessons

`file://` works for reading, but **`playwright-cli` blocks the `file:` protocol**,
so serve the workspace when you need to drive or verify a lesson:

```bash
cd teaching && python3 -m http.server 8777 --bind 127.0.0.1
# http://127.0.0.1:8777/lessons/0001-efficiency-or-correctness.html
```

## Conventions

- Every lesson links `assets/lesson.css`. Never inline styles a second lesson
  would duplicate.
- Lessons are `lessons/NNNN-dash-case.html`, numbered sequentially.
- Quiz answers must be **the same length** so formatting gives nothing away.
- Under 10 minutes each. Working memory is the binding constraint, not coverage.
- **Verify a lesson actually renders before claiming it works.** Check the
  computed style and the widget's node count, not just that the file was
  written — a silently truncated stylesheet looks identical to a missing one.
- Prefer scenarios from our own stack — EF Core, Npgsql, Service Bus, Azure
  blobs — over generic textbook examples.
