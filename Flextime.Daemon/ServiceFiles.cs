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
            builder.AppendLine("    <key>KeepAlive</key>");
            builder.AppendLine("    <true/>");
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

        var arguments = string.Join(' ', command.Skip(1).Select(item => item.Contains(' ') ? $"\"{item}\"" : item));

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
        string.Join(' ', command.Select(item => item.Contains(' ') ? $"\"{item}\"" : item));
}
