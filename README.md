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
