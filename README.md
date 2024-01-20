Cross plattform implementation of flextime, a program for tracking working hours.

The program use D-Bus on Linux, and `CGEventSourceSecondsSinceLastEventType` on macOS.  Windows is not currently supported, but would probably P/Invoke `GetLastInputInfo`.