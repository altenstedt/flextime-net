using System.Reflection;

namespace Flextime.Daemon;

/// <summary>
/// The agent skill that reads Flextime data: printed, or written where an
/// agent looks for skills.  It is built into the binary, so the skill a
/// user installs always describes the flextimed they have.
/// </summary>
public static class Skill
{
    public const string HelpKey = """

        Without options it prints the skill to standard out, to read or copy.  It
        teaches an AI agent to run data, computers and export, and what the
        numbers mean; it only reads.  Install it again after updating flextimed.
        """;

    // Where Claude Code looks for the user's own skills, in any folder.
    public static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "skills", "flextime");

    public static string Text
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Flextime.Daemon.SKILL.md")
                ?? throw new InvalidOperationException("The skill is missing from this build.");
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        }
    }

    public static int Invoke(bool install, string? output)
    {
        if (install && output != null)
        {
            Console.Error.WriteLine("Give either --install or --output, not both.");
            return 1;
        }

        var folder = install ? InstallFolder : output;

        if (folder == null)
        {
            Console.Out.Write(Text);
            return 0;
        }

        var path = Write(folder);
        Console.WriteLine($"Wrote {path}.");
        return 0;
    }

    // A copy already there is replaced: it came from an older flextimed.
    public static string Write(string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "SKILL.md");
        File.WriteAllText(path, Text);
        return path;
    }
}
