Cross plattform implementation of flextime, a program for tracking
working hours.

The Flextime daemon query the computer for the time since the last
user input once every minute and stores the result on disk.  The
flextime program can be used to display the times the user has been
active on the computer.  For example:

    $ flextime
    2020-08-10 07:16 — 16:10 8:53 | 0:27 w/33 Mon
    2020-08-11 13:29 — 14:01 0:31 | 0:12 w/33 Tue
    2020-08-12 07:33 — 17:22 9:49 | 6:22 w/33 Wed
    2020-08-13 07:54 — 15:17 7:22 | 3:34 w/33 Thu
    2020-08-14 07:29 — 10:38 3:08 | 3:08 w/33 Fri

If your working day starts and ends on the computer, this list
effectively represents your working hours.

The flextime program only reads the files on this computer; it never
uses the network and needs no account.  It shows the last 30 days by
default; `--days 0` shows every day on disk, `--idle` sets the idle
limit in minutes, and `--json` writes the same shape as
`flextimed data --json`, so a script can read either one.

`--since` takes a length of time written any of the usual ways, in
English:

| Form | Examples |
| --- | --- |
| .NET TimeSpan | `3`, `3.00:00:00`, `12:00:00` |
| Compact units | `3d`, `2w`, `1h30m`, `1d12h`, `90m` |
| Words | `3 days`, `2 weeks ago`, `90 minutes`, `1 hour` |
| ISO 8601 duration | `P3D`, `P2W`, `PT90M`, `P1DT12H` |
| Keywords | `today`, `yesterday`, `this week`, `last week` |

A trailing `ago` is accepted and ignored; there is only one direction
to look.  `this week` and `last week` are ISO weeks, starting Monday,
matching the week numbers in the day lines.  Whole days are all this
prints, so anything shorter than a day means today, and the colon
forms keep their .NET meaning — `36:00:00` is 36 days, `36h` is 36
hours.  Months and years are not accepted: neither has a fixed length.

Just make sure that the Flextime daemon, is started every time you
log in.

The program use D-Bus on Linux, and
`CGEventSourceSecondsSinceLastEventType` on macOS, and
`GetLastInputInfo` on Windows.

Publish:

```sh
dotnet publish -c Release --use-current-runtime --self-contained
```

# protobuf

We use Google's protobuf implementation.

If you want to change the storage format, you will want to edit file
`measurement.proto` and then regenerate the C# code:

```
$ protoc measurement.proto --csharp_out=./Flextime
```

* https://grpc.io/docs/protoc-installation/
* https://github.com/protocolbuffers/protobuf/tree/main/csharp

# CLI refactor, needed again after Powderhouse

* https://github.com/orgs/dotnet/projects/381
* https://github.com/dotnet/command-line-api/labels/Powderhouse
* https://github.com/dotnet/command-line-api/issues/2338
* https://github.com/dotnet/command-line-api/issues/440#issuecomment-2024850186
* https://github.com/dotnet/command-line-api/issues/556
