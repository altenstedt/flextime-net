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

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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

        if (install)
        {
            var (path, link) = Install(Home);
            Console.WriteLine($"Wrote {path}.");
            if (link != null)
            {
                Console.WriteLine($"Linked {link} to it, for Claude Code.");
            }
            return 0;
        }

        if (output == null)
        {
            Console.Out.Write(Text);
            return 0;
        }

        Console.WriteLine($"Wrote {Write(output)}.");
        return 0;
    }

    /// <summary>
    /// Writes the skill to ~/.agents/skills/flextime, where the agents
    /// that share the Agent Skills folder look for a user's skills.  Claude
    /// Code reads only ~/.claude/skills, so where ~/.claude exists the
    /// skill is linked from there too: one copy to update, read by both.
    /// Returns the file written and the link made, if any.
    /// </summary>
    public static (string Path, string? Link) Install(string home)
    {
        var folder = Path.Combine(home, ".agents", "skills", "flextime");
        var path = Write(folder);

        var claude = Path.Combine(home, ".claude");
        if (!Directory.Exists(claude))
        {
            return (path, null);
        }

        var link = Path.Combine(claude, "skills", "flextime");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        // An earlier flextimed wrote a copy here; a link from before is
        // made again, in case it points somewhere else.
        var existing = new DirectoryInfo(link);
        if (existing.LinkTarget != null)
        {
            existing.Delete();
        }
        else if (existing.Exists)
        {
            if (existing.GetFileSystemInfos().Any(item => item.Name != "SKILL.md"))
            {
                // Not ours alone: write the copy there instead of
                // deleting what the user put beside it.
                Write(link);
                return (path, null);
            }
            existing.Delete(recursive: true);
        }

        try
        {
            Directory.CreateSymbolicLink(link, folder);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Windows makes links only with developer mode or as an
            // administrator; a copy serves as well, it is only one more
            // to update.
            Write(link);
        }
        return (path, link);
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
