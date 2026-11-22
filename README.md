Cross platform implementation of flextime, a program for tracking
working hours.

The Flextime daemon queries the computer for the time since the last
user input once every minute and stores the result on disk.  The
flextime program can be used to display the times the user has been
active on the computer.  For example:

    $ flextime
    2020-08-10 07:16 – 16:10 08:53 | 00:27 w/33 Mon
    2020-08-11 13:29 – 14:01 00:31 | 00:12 w/33 Tue
    2020-08-12 07:33 – 17:22 09:49 | 06:22 w/33 Wed
    2020-08-13 07:54 – 15:17 07:22 | 03:34 w/33 Thu
    2020-08-14 07:29 – 10:38 03:08 | 03:08 w/33 Fri

If your working day starts and ends on the computer, this list
effectively represents your working hours.

The flextime program only reads the files on this computer; it never
uses the network and needs no account.  It shows the last 30 days by
default; `--days 0` shows every day on disk, `--idle` sets the idle
limit in minutes (10 by default), and `--json` writes the days per
computer as JSON.

`--since` takes a length of time written any of the usual ways, in
English:

| Form | Examples |
| --- | --- |
| .NET TimeSpan | `3`, `3.00:00:00`, `12:00:00` |
| Compact units | `3d`, `2w`, `1h30m`, `1d12h`, `90m` |
| Words | `3 days`, `2 weeks ago`, `90 minutes`, `1 hour` |
| ISO 8601 duration | `P3D`, `P2W`, `PT90M`, `P1DT12H` |
| Keywords | `today`, `yesterday`, `this week`, `last week` |

The interval options on the daemon — `sync --every`, `install --every`
and `listen --log-summary-interval` — read the same lengths, except the
keywords: they name one particular day, and a day cannot repeat.

A trailing `ago` is accepted and ignored; there is only one direction
to look.  `this week` and `last week` are ISO weeks, starting Monday,
matching the week numbers in the day lines.  Whole days are all this
prints, so anything shorter than a day means today, and the colon
forms keep their .NET meaning — `36:00:00` is 36 days, `36h` is 36
hours.  Months and years are not accepted: neither has a fixed length.

Run the daemon at every login with:

    flextimed install           # listen, and sync --once every 20m
    flextimed install --every 1h
    flextimed uninstall         # measurements and sign-in are kept

On macOS that is two launchd agents, `se.flextime.listen` and
`se.flextime.sync`, in `~/Library/LaunchAgents`, logging to
`~/Library/Logs/Flextime`.  On Linux it is the systemd user units
`flextime-listen.service` and `flextime-sync.timer`; on Windows two
scheduled tasks, and there `--time-zone` is required.  Run without a
command, `flextimed` shows the installation and whether it is healthy;
`--days` also lists the latest days.

`flextimed stop` stops `listen`, across logins, until `flextimed
start`; sync keeps going.  To leave a stretch out for a while instead:

    flextimed pause                 # until midnight
    flextimed pause --for 2h
    flextimed pause --until 07:00
    flextimed resume

A pause ends by itself, never later than the end of tomorrow.  The
active stretch just before it, if no longer than 30 minutes, is marked
as not work; `--since` marks from the time you give instead, and
`--no-backdate` marks nothing.  The mark is undone in the web app.  A
pause shorter than the idle limit is bridged like any other gap.

`flextimed listen` is what the service runs; `listen --help` has its
options.  `FLEXTIME_LOG_LEVEL` (`Debug`, `Warning`, ...) sets how much
the daemon logs.

The measurements are kept under `Flextime/measurements` in the local
application data folder (`~/Library/Application Support` on macOS,
`~/.local/share` on Linux, `%LOCALAPPDATA%` on Windows), with the
sign-in and the daemon's state next to them in `Flextime`.

`flextimed login` signs in with a device code and `flextimed logout`
removes the sign-in; the measurements are kept.  Once you log in,
`flextimed sync --once` uploads, and `sync --every 20m` keeps doing
so.  Sync uploads the measurements with their time zone, a random id
for the computer, and the computer's hostname, read afresh on every
pass: on macOS the computer name from System
Settings, elsewhere `Environment.MachineName`.  The server keeps the
hostnames each computer reported.

A computer can also have a name of your own, kept on the server, which
the web and these commands show in place of the hostname:

```sh
flextimed computers                       # every computer, dormant ones last
flextimed computer                        # this one: name, hostname, former names
flextimed computer rename "Work 2024"     # this computer
flextimed computer rename "Old iMac" --id 0a1b2c3d4e5f6a7b
flextimed computer rename ""              # back to the hostname
```

A computer with no data in 90 days is dormant; `computers` lists it
last.  Its data is all still there.  `computers` also lists the ids
`data --computer` takes, and marks this computer with `*`, or with
`"here": true` in `--json`.

`flextimed data` shows your hours as the web app counts them: the
computers chosen merged into one day list, each day on its idle
profile, less the time marked as not work, plus the time added by
hand.  Work, start and end match the web's day rows to the minute, and
a dot marks a day with its own profile or added time:

```sh
flextimed data                            # this computer, the last 30 days
flextimed data --month this --all-computers
flextimed data -w 45 -c 0a1b2c3d4e5f6a7b,1b2c3d4e5f6a7b8c
```

```text
MacBook Pro M4, Omegapoint

November 2026
Week 45
  Mon  2   07:54 – 21:35   08:05
  Fri  6   08:27 – 17:00   01:55 •
                           27:46
Week 47
  Fri 20   07:45 – 18:26   08:35 •
  Sat 21                   not work
                           35:55
November                  102:47
```

`--since`, `--week` and `--month` read as in `report`.  `--json` writes
the computers and one entry per day, with its date, zone, start, end,
work, and whether it is custom or marked whole as not work; the totals
are sums of the days.

`flextimed export` saves your data on the server to one JSON file, `-o
-` to standard out: every computer, every timestamp, and your settings.
`--since`, `--week` or `--month` limit the activity to those days; the
settings are saved whole all the same, and the file's `about` says
which days it holds.

The program uses D-Bus on Linux, and
`CGEventSourceSecondsSinceLastEventType` on macOS, and
`GetLastInputInfo` on Windows.

Publish, with .NET 11, as native AOT:

```sh
dotnet publish Flextime.Client -c Release --use-current-runtime
dotnet publish Flextime.Daemon -c Release --use-current-runtime
```

and install the two binaries as `~/.local/bin/flextime` and
`~/.local/bin/flextimed`.

# Reconciling reported hours

The daemon's `report` command compares the hours you reported — to a
client, an employer, an invoicing system — against the activity the
server measured, and shows where they disagree.

The reported side is yours to provide: a JSON file with one entry per
day and tag, written by hand or exported from whatever you report your
time in.  The format is deliberately small so that anything can
produce it:

```json
[
  { "date": "2026-08-17", "tag": "ClientA", "minutes": 480 },
  { "date": "2026-08-18", "tag": "ClientA", "minutes": 510 },
  { "date": "2026-08-18", "minutes": 30 }
]
```

The tag is optional and means whatever your reporting means by it — a
client, a project.  Then:

    flextimed report --timesheet hours.json --month this

or pipe the timesheet in.  The command needs a logged-in daemon, since
the measured side comes from the server, and it applies your stored
idle profiles, marks and added time so the numbers match what the web
client showed when you wrote the hours down.  `report --help`
describes the window, machine and layout options; running `flextimed report` with no timesheet
prints a primer much like this section.

The reported hours are summed per tag under `Tags:`, and `--tags`
picks the layout:

    total     one line per tag over the whole window (the default)
    matrix    days down, tags across; one week and five tags at most
    days      each tag's sum, then the days that make it up
    weeks     a block per week, each tag's days as columns Mon to Sun
    months    a block per month, its sum, then each tag's share of it

The other sections are the same whichever layout you pick.  Over more
than one month, `months` ends with a total for the window:

    flextimed report --timesheet hours.json --month jun-aug --tags months

    Tags:
    2026-06              168:00
      ClientA            160:00
      ClientB             08:00
    2026-07               84:00
      ClientA             84:00
    2026-08              176:00
      ClientA            152:00
      ClientB             24:00
    total                428:00

# Places

The daemon can record where the measured time was spent — not a
position, but the network's router — so the web client can color the
day by place.  It is off until you turn it on:

    flextimed location on       # requires login
    flextimed location          # on or off, and the place right now
    flextimed location --name   # also the name given in the web app
    flextimed location --help   # what each line means
    flextimed location off      # the time after this has no place
    flextimed location clear    # remove the place history, here and on the server

`listen` reads the setting every minute, so no restart is needed.  A
place is written into the measurement files only when it changes, as a
48-bit hash of your user id and the router's hardware address, with
flags for a connected VPN, for traffic through a tunnel and for a
metered network; sync sends both with each change point.  The API keys
that hash again before it stores anything.  The hash hides the router's address from anyone
without your user id, but not from the server: it knows the id, and
can try every address within the known vendor prefixes.  The first
time a file records a router it also notes the network's DNS domain,
if the network gives one (like `omegapoint.se`), as a hint for naming
the place; sync sends it with that change point.  Router defaults such
as `localdomain`, `lan` or `home` name no place and are left out.
With location off the files are exactly what they were before places
existed, and sync sends exactly what it did.  `clear --all-computers`
clears every computer on the account on the server; the others clear
their own files when they next sync.  `flextimed network` shows what
the daemon can tell about the network, and stores nothing.

# protobuf

We use Google's protobuf implementation.

If you want to change the storage format, you will want to edit file
`measurement.proto` and then regenerate the C# code:

```
$ protoc measurement.proto --csharp_out=./Flextime
```

The `protoc` inside the `Grpc.Tools` NuGet package works too
(`tools/macosx_universal/protoc`); version 35.1 generated the current
file.

* https://grpc.io/docs/protoc-installation/
* https://github.com/protocolbuffers/protobuf/tree/main/csharp
