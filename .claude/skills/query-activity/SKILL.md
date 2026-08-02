---
name: query-activity
description: Query Flextime activity data stored on the server — per-day working hours per computer. Use when asked about recorded working hours, activity data, or what's on the server. Read-only.
---

# Query Flextime activity data from the server

Use the daemon's `data` command. It handles authentication (token refresh) and
talks to the server API — never read the token file or call the API directly.

```sh
dotnet run --project Flextime.Daemon -- data --json [options]
```

(Run from the repository root. If a published `Flextime.Daemon` binary is on
PATH, use it directly instead — it starts faster.)

## Options

| Option | Meaning |
|---|---|
| `--json` | Pure JSON on stdout. Always use this; without it you get a human-readable listing. |
| `-d, --days <N>` | Last N days (default 30). |
| `-c, --computer <id>` | Computer id(s), comma separated. Defaults to this machine. |
| `--all-computers` | All of the user's computers. |
| `-i, --idle <min>` | Idle limit in minutes (default 10). |
| `--timestamps` | Also include each day's raw timestamps (Unix seconds). Omit unless needed — days can hold ~500 entries each. |

## Output

```json
{
  "items": [
    {
      "id": "f204899922822897",
      "name": "MacBook Pro M4, Omegapoint",
      "days": [
        {
          "date": "2026-07-17",
          "zone": "Europe/Berlin",
          "start": "2026-07-17T07:41:00+02:00",
          "end": "2026-07-17T15:20:00+02:00",
          "span": "07:39:00",
          "work": "06:18:00",
          "measurements": 315
        }
      ]
    }
  ]
}
```

- `start`/`end` — first and last recorded activity of the day, in the day's `zone`.
- `span` — `end - start`. `work` — active time: sum of gaps between consecutive
  measurements that are no longer than the idle limit (inclusive; default
  10 minutes). The `flextime` CLI and the web client use the same rule, so
  numbers match across clients at the same limit.
- `measurements` — number of recorded samples (roughly one per active minute).

## Exit codes

- `0` — success, stdout is pure JSON.
- `2` — not signed in (or sign-in expired). Ask the user to run the daemon's
  `login` command; it is an interactive device-code flow you cannot complete
  for them.
- `1` — network error or unknown computer id; message on stderr.

## Caveats

- The command is read-only and shows **server-side data only**. This machine's
  local measurements reach the server only when the daemon syncs, so recent
  days may be missing or incomplete. If the user wants fresh numbers for this
  machine, suggest running the daemon's `sync --once` command first (it uploads
  local data — get their go-ahead), or use the local `flextime` CLI instead.
- Days with fewer than 2 measurements are omitted.
