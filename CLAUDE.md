# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

Talk material for a series of internal knowledge-sharing sessions on system
design. **The deliverable is Markdown; the code exists as evidence for it.**
The `.cs` files are not a product — they are demos that prove the claims in
the documents, and several of them exist specifically to falsify things
"everyone knows" about locking.

There is no build, no lint, and no test suite. Do not go looking for one.

## The verification standard — the core convention

Every factual claim in this repo is either sourced to a primary document or
reproduced by a runnable demo. This is not decoration; it is the reason the
repo exists. During the initial research **seven of seven** commonly-repeated
claims about locking turned out to be wrong, including a live documentation
bug on Microsoft Learn.

Consequently:

- **Do not add a claim you have not verified.** If you cannot verify it, write
  it under an explicit "Unverified / open" heading rather than asserting it.
- **Prefer running the thing over reading about it.** Multiple findings in
  `research/` were corrected by actually standing up Postgres, EF Core, or a
  Windows process and observing the behaviour. Where a research file was later
  contradicted by experiment, it carries a correction banner near the top —
  preserve those rather than quietly editing the original claim away.
- **Numbers quoted in the docs came from real runs.** If you change a demo in
  a way that changes its output, update the numbers quoted in `FRAMEWORK.md`,
  `TALK.md`, `NOTES.md` and `demos/README.md`.

## Document structure — which file to edit

The four document types are not drafts of each other. They have distinct jobs:

| File | Job | Edit it when |
|---|---|---|
| `NN-topic/FRAMEWORK.md` | The durable artifact devs bookmark: the mechanism map, the questions, the decision tree, anti-patterns | The *guidance* changes |
| `NN-topic/TALK.md` | A timed run sheet for presenting — timings, what's on screen, what to say, a cut list | The *presentation* changes |
| `NN-topic/NOTES.md` | Deep source material, all mechanisms, far more than fits the talk | You have depth that doesn't fit the run sheet |
| `NN-topic/research/*.md` | Primary-source findings, one file per question, with a Sources section | New research lands |
| `NN-topic/slides/` | The deck presented from. Self-contained HTML, no build step | The talk's structure changes |

`FRAMEWORK.md` is the primary artifact. `TALK.md` teaches it. `NOTES.md` is
where detail goes that the talk can't hold. Research files are inputs to all
three and should not be rewritten to match a doc — the doc changes to match
the research.

## Session scope boundaries

Sessions deliberately defer topics to each other; respect this when adding
content. Session 1 (Locking) **points at** idempotency and `FOR UPDATE SKIP
LOCKED` but does not teach them — both belong to session 3 (Competing Consumer
& Idempotency). Widening session 1 back into general concurrency control has
already been rejected once.

## Running the demos

```bash
cd 01-locking/demos

aspire run                              # Postgres + Redis. ~60s to ready.
dotnet run 01-counter.cs                # demos 1-2 need nothing; 03 needs Docker for 3/4
dotnet run 06-redis-lock.cs -- --naive  # wrong variants live behind flags
./03-mutex-scope.sh 1 2                 # takes scenario numbers; all four takes ~2 min
dotnet publish 03-mutex-b.cs -o out     # file-based apps do publish, used for the container test
```

Requires .NET SDK **10.0.300+** (file-based apps), the Aspire CLI, and Docker.

Demos are **file-based apps** — a single `.cs` with `#:package` / `#:property`
/ `#:include` directives and no `.csproj` anywhere. That is deliberate: each
demo must fit on one screen for an audience. Keep it that way; if a demo
outgrows the format, `dotnet project convert` exists but is a last resort.

Three demos are used in the talk (`03-mutex-scope.sh`,
`10-efcore-pooling.cs`, `07-expiry.cs`) and climb the scope ladder — one machine, one database, the
fleet. The rest are reference. `demos/README.md` says which is which.

## Aspire AppHost — non-obvious setup

`apphost.cs` is a single-file Aspire AppHost. Three decisions in it were
arrived at the hard way and will look wrong if you don't know why:

- **`IsProxied = false` is required to pin host ports.** `WithHostPort` alone
  is silently ignored — Aspire's DCP proxy assigns random ports regardless.
- **Ports are pinned high on purpose** (Postgres 55432, Redis 56379) so they cannot collide with anything already on a developer's
  machine. Demos connect with plain `localhost` strings via `connection.cs`
  rather than Aspire-injected ones, so each script can be launched by hand
  mid-talk.
- **Redis is a plain `AddContainer`, not `AddRedis`.** Aspire's Redis
  integration defaults to TLS on 6379 plus a generated `--requirepass`, which
  is right for a real app and stops you inspecting keys with `redis-cli` on
  stage.
**PgBouncer was deliberately removed** — the team doesn't run it. The same bug
is demonstrated one layer up by `10-efcore-pooling.cs`, using EF Core on
Npgsql's default pool, which is their actual stack. That demo pins
`Maximum Pool Size=1` so the second request deterministically draws the same
connection; without it you get a hang instead of the silent violation.
`research/02` keeps the PgBouncer material for reference — don't reintroduce
it as talk content.

`aspire.config.json` is generated by `aspire run` and is gitignored.

## Conventions

- **Direct commits to `main` are blocked by a hook.** Branch, then open a PR.
- **Keep company names out of committed content.** The repo is public.
- Demos put their wrong variant behind a `--flag` rather than in a second
  file, so a presenter toggles on stage instead of switching files.
