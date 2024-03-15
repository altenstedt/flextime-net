Cross plattform implementation of flextime, a program for tracking
working hours.

The program use D-Bus on Linux, and
`CGEventSourceSecondsSinceLastEventType` on macOS, and
`GetLastInputInfo` on Windows.

Publish:

```sh
dotnet publish -c Release --use-current-runtime --self-contained
```