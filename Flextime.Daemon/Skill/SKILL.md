---
name: flextime
description: Read the user's own working hours from Flextime with the flextimed command, and explain what they mean. Use when asked about hours worked, a day, week or month of work, timesheets, overtime, or building a report or tool on top of Flextime data. Read-only.
---

# Flextime data

Flextime measures when a computer is in use, once a minute, and keeps the
measurements on a server.  `flextimed` reads them back, signed in as the
user.  This skill came with flextimed.  If this copy may be out of date,
`flextimed skill` prints the text of the skill that matches the flextimed
installed.

## Commands

All of these only read.

- `flextimed computers --json` lists the user's computers:
  `{ "items": [{ "id", "name", "hostname", "firstDay", "lastDay", "dormant", "here" }] }`.
  The entry with `"here": true` is the computer you run on.  A dormant
  computer has had no data for a while.
- `flextimed data --json` lists the user's hours, day by day, as the web app
  at https://flextime.inhill.com/ shows them.
  - Days: `--since 30d` (the default; also `1w`, `P7D`, `yesterday`),
    `--week this` (also `last`, `47`, `45-47`, `2026-W47`) or
    `--month this` (also `last`, `nov`, `sep-nov`, `11`, `2026-11`).
    `--since` runs from that day through today: `--since yesterday` gives
    yesterday and today, and `30d` gives 31 dates.  In `--since`, `m` is
    minutes, not months: use `--month` for months.
  - Computers: `-c id,id`, or `--all-computers`.  The default is this
    computer.  The computers chosen are merged: time on two at once counts
    once.
  - `--hide-not-work` leaves out days marked as not work.
- `flextimed export -o - --month 2026-11` writes the raw measurements and the
  user's settings as one JSON document.  Without a range it holds the whole
  history, which can be several megabytes.

Without `--json`, data prints a table for people; parse only the JSON.

## data --json

```json
{
  "computers": [{ "id": "f204899922822897", "name": "Work laptop" }],
  "days": [{
    "date": "2026-11-20",
    "zone": "Europe/Stockholm",
    "start": "2026-11-20T07:45:00+01:00",
    "end": "2026-11-20T18:26:00+01:00",
    "work": "08:35:12",
    "custom": true,
    "notWork": false
  }]
}
```

- `days` run oldest first.  A date with no entry had no use of the chosen
  computers, or a single minute; time added by hand to such a date is not
  shown, as in the web.
- `work` is the time counted as work that day, as `hh:mm:ss`.  The web shows
  it rounded to the minute, and rounds a week's or month's total once, over
  the exact sum.  Do the same, and totals will match the web.
- `start` and `end` are the first and last minute counted, in the day's own
  zone, including time the user added by hand.  On a day marked whole as
  not work, they are the first and last minute measured.
- `custom` means the user changed the day: its own idle profile, or time
  added by hand.
- `notWork` means the whole day is marked as not work; its `work` is zero.
  Time marked as not work within a day is already left out of `work`.
- `zone` is the time zone the day was measured in; it changes when the user
  travels.

How `work` is counted: a gap in use shorter than the idle limit still counts
as work, a longer one does not.  The user sets the limit per weekday, or for
a single day, as an idle profile in the web app.  Days and ranges marked as
not work are taken out, and time added by hand is put in.  All of this is
already applied: never recompute hours from the raw timestamps, the numbers
would not match what the user sees.

## export

The export file explains itself in its `about` field.  In short:
`computers`; `activity`, one entry per computer with its `days`, each a
`zone`, `timestamps` (Unix seconds, one per minute in use) and, with
location on, `places`; `hints`; and `settings`, the user's idle profiles
(`profiles`) and place names (`places`) as the web app stores them, and
`days`, one entry per date with not-work marks (`marks`) or time added by
hand (`added`), each with a `value` and a `version`.  A marks `value` is
`"day"` for the whole day, or a list of `{ "from", "to" }`; an added `value`
has `started`, `ended` and `ranges`, each optional.  Times are seconds of
the day in the day's own zone.  A range limits the activity and `days`;
profiles and place names are always whole.

Use export for questions data cannot answer, such as when in the day the
computer is used, or how long the breaks are.

## Rules

- Use `data` for hours.  Use `export` only for what `data` does not have.
- Never read flextimed's token files or call the server yourself.  If
  flextimed says the user is not logged in, ask them to run
  `flextimed login`.
- Run only the commands above, and `flextimed skill` without options.
  Others change the user's setup or data on the server (`pause`, `resume`,
  `location on`, `location off`, `location clear`, `computer rename`,
  `login`, `logout`, `sync`, `listen`, `install`, `uninstall`, `stop`,
  `start`, `skill --install`): suggest them, never run them.
- Measured hours are not hours worked or charged.  Travel, meetings away
  from the computer and agreements with a customer are not in Flextime;
  users often keep them in their own notes.  Ask before reading such a
  file, and keep what is in it on this computer.
- Flextime has no calendar.  It knows nothing of holidays, leave or the
  hours a user owes; ask the user when an answer depends on them.
- When a number differs from the web app, check that the same computers are
  chosen: the web keeps its choice in the browser.
- The data is the user's own and personal.  Do not send it anywhere they
  have not asked you to.
