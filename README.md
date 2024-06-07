Cross plattform implementation of flextime, a program for tracking
working hours.

The program use D-Bus on Linux, and
`CGEventSourceSecondsSinceLastEventType` on macOS, and
`GetLastInputInfo` on Windows.

Publish:

```sh
dotnet publish -c Release --use-current-runtime --self-contained
```

# CLI refactor, needed again after Powderhouse

* https://github.com/orgs/dotnet/projects/381
* https://github.com/dotnet/command-line-api/labels/Powderhouse
* https://github.com/dotnet/command-line-api/issues/2338
* https://github.com/dotnet/command-line-api/issues/440#issuecomment-2024850186
* https://github.com/dotnet/command-line-api/issues/556
