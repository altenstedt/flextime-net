using System.Security;
using System.Text;
using System.Xml;

namespace Flextime.Daemon;

/// <summary>
/// Renders the service definition files written by the install command.
/// Pure string builders so that they can be tested on any platform.
/// </summary>
public static class ServiceFiles
{
    public static string LaunchAgentPlist(string label, IReadOnlyList<string> command, string logPath, bool keepAlive, TimeSpan? startInterval)
    {
        var builder = new StringBuilder();

        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        builder.AppendLine("""<plist version="1.0">""");
        builder.AppendLine("<dict>");
        builder.AppendLine("    <key>Label</key>");
        builder.AppendLine($"    <string>{SecurityElement.Escape(label)}</string>");
        builder.AppendLine("    <key>ProgramArguments</key>");
        builder.AppendLine("    <array>");

        foreach (var argument in command)
        {
            builder.AppendLine($"        <string>{SecurityElement.Escape(argument)}</string>");
        }

        builder.AppendLine("    </array>");
        builder.AppendLine("    <key>RunAtLoad</key>");
        builder.AppendLine("    <true/>");

        if (keepAlive)
        {
            // Restart on crash only.  A clean exit, like the single instance
            // guard declining to start, must not restart-loop.
            builder.AppendLine("    <key>KeepAlive</key>");
            builder.AppendLine("    <dict>");
            builder.AppendLine("        <key>SuccessfulExit</key>");
            builder.AppendLine("        <false/>");
            builder.AppendLine("    </dict>");
        }

        if (startInterval.HasValue)
        {
            builder.AppendLine("    <key>StartInterval</key>");
            builder.AppendLine($"    <integer>{(int)startInterval.Value.TotalSeconds}</integer>");
        }

        builder.AppendLine("    <key>StandardOutPath</key>");
        builder.AppendLine($"    <string>{SecurityElement.Escape(logPath)}</string>");
        builder.AppendLine("    <key>StandardErrorPath</key>");
        builder.AppendLine($"    <string>{SecurityElement.Escape(logPath)}</string>");
        builder.AppendLine("</dict>");
        builder.AppendLine("</plist>");

        return builder.ToString();
    }

    public static string SystemdListenService(IReadOnlyList<string> command) =>
        $"""
         [Unit]
         Description=Flextime listen
         After=graphical-session.target
         PartOf=graphical-session.target

         [Service]
         ExecStart={SystemdCommand(command)}
         Restart=on-failure
         RestartSec=10

         [Install]
         WantedBy=graphical-session.target

         """;

    public static string SystemdSyncService(IReadOnlyList<string> command) =>
        $"""
         [Unit]
         Description=Flextime sync

         [Service]
         Type=oneshot
         ExecStart={SystemdCommand(command)}

         """;

    public static string SystemdSyncTimer(TimeSpan every) =>
        $"""
         [Unit]
         Description=Flextime sync timer

         [Timer]
         OnStartupSec=60
         OnUnitActiveSec={(int)every.TotalSeconds}

         [Install]
         WantedBy=timers.target

         """;

    public static string WindowsTaskXml(string description, string userId, IReadOnlyList<string> command, TimeSpan? repetition, TimeSpan? executionTimeLimit)
    {
        var user = SecurityElement.Escape(userId);

        // The schema requires Repetition before the other trigger elements.
        var repetitionElement = repetition.HasValue
            ? $"""
                    <Repetition>
                      <Interval>{XmlConvert.ToString(repetition.Value)}</Interval>
                      <StopAtDurationEnd>false</StopAtDurationEnd>
                    </Repetition>

              """
            : string.Empty;

        // PT0S means no limit.
        var limit = XmlConvert.ToString(executionTimeLimit ?? TimeSpan.Zero);

        var arguments = string.Join(' ', command.Skip(1).Select(WindowsArgument));

        RejectControlCharacters(command[0]);

        return $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Description>{SecurityElement.Escape(description)}</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                {repetitionElement}      <Enabled>true</Enabled>
                      <UserId>{user}</UserId>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <UserId>{user}</UserId>
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>LeastPrivilege</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>true</AllowHardTerminate>
                    <StartWhenAvailable>true</StartWhenAvailable>
                    <AllowStartOnDemand>true</AllowStartOnDemand>
                    <Enabled>true</Enabled>
                    <Hidden>false</Hidden>
                    <ExecutionTimeLimit>{limit}</ExecutionTimeLimit>
                    <RestartOnFailure>
                      <Interval>PT1M</Interval>
                      <Count>10</Count>
                    </RestartOnFailure>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{SecurityElement.Escape(command[0])}</Command>
                      <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;
    }

    private static string SystemdCommand(IReadOnlyList<string> command) =>
        string.Join(' ', command.Select(SystemdArgument));

    /// <summary>
    /// One word of an ExecStart line.  systemd expands % specifiers and
    /// $ variables everywhere on the line, and reads C escapes inside
    /// quotes; each is escaped so the word arrives as written.
    /// </summary>
    public static string SystemdArgument(string argument)
    {
        RejectControlCharacters(argument);

        var escaped = argument.Replace("%", "%%").Replace("$", "$$");

        var quote = argument.Length == 0
                    || argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'' or '\\' or ';');

        return quote
            ? $"\"{escaped.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""
            : escaped;
    }

    /// <summary>
    /// One argument of a Windows command line, quoted the way the C
    /// runtime's parser (CommandLineToArgvW) splits it back: backslashes
    /// count only before a quote, where they are doubled.
    /// </summary>
    public static string WindowsArgument(string argument)
    {
        RejectControlCharacters(argument);

        if (argument.Length > 0 && !argument.Any(character => character is ' ' or '\t' or '"'))
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            builder.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            builder.Append(character);
            backslashes = 0;
        }

        // Before the closing quote, so that it stays a quote.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');

        return builder.ToString();
    }

    // A line break would end the unit's line, or the task's argument
    // list, and start one the argument was never meant to write.
    private static void RejectControlCharacters(string argument)
    {
        if (argument.Any(char.IsControl))
        {
            throw new ArgumentException("A service command cannot hold control characters.", nameof(argument));
        }
    }
}
