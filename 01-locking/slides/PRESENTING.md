# Presenting this deck

```sh
cd 01-locking/slides && python3 -m http.server 8778
# then open http://127.0.0.1:8778 and press f
```

`file://` works too — the deck is a single self-contained HTML file with no
dependencies, no build, and no network access.

## Controls

| | |
|---|---|
| `→` `space` `PageDown` | next |
| `←` `PageUp` | previous |
| `n` | toggle speaker notes · `Esc` closes |
| `f` | fullscreen |
| `Home` / `End` | first / last slide |
| click | next — except on code, tables and links |

A standard presentation clicker sends `PageUp`/`PageDown`, so it works
unmodified.

## Speaker notes are in-window

Pressing `n` overlays the notes on the **same** screen, which means the
audience sees them if you are mirroring. Two ways to handle it:

- **Rehearse with them, present without.** Every note is also in
  [`../TALK.md`](../TALK.md), which is the fuller version.
- **Extend the display** rather than mirroring, and keep the deck on the
  projector while `TALK.md` is open on your laptop.

## The deck does not replace the demos

Slides 5, 8 and 11 are **title cards**: they put the command on screen, then
you switch to a terminal. The slide after each one carries the takeaway, so the
audience still has it once the terminal is gone.

Warm every demo before the room is watching — file-based apps compile on first
run:

```sh
cd ../demos && aspire run
./03-mutex-scope.sh 1 2
dotnet run 10-efcore-pooling.cs
dotnet run 07-expiry.cs
```

## Printing

`Ctrl-P` gives one slide per page on a light background, notes omitted. Useful
as a backup if the projector fights you.

## Timings

23 slides against the [six sections of `TALK.md`](../TALK.md#timings):

| Slides | Section | Min |
|---|---|---|
| 1–4 | The problem | 2.5 |
| 5–7 | Demo — one machine | 3.5 |
| 8–10 | Demo — one database | 3.5 |
| 11–13 | Demo — the fleet | 3.5 |
| 14–18 | **The framework** | 8 |
| 19–23 | Wrap | 4 |

Running long, cut slides 5–7. Never cut 14–18.
