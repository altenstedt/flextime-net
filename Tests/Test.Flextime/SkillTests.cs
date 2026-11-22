using Flextime.Daemon;

namespace Test.Flextime;

public class SkillTests
{
    [Fact]
    public void The_skill_is_built_in_and_names_itself()
    {
        Assert.StartsWith("---\nname: flextime\n", Skill.Text.ReplaceLineEndings("\n"));
        Assert.Contains("flextimed data --json", Skill.Text);
    }

    [Fact]
    public void Writing_replaces_an_older_copy()
    {
        var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName(), "flextime");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "SKILL.md"), "old");

            var path = Skill.Write(folder);

            Assert.Equal(Skill.Text, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
        }
    }

    [Fact]
    public void Install_and_output_together_are_refused() =>
        Assert.Equal(1, Skill.Invoke(install: true, output: "somewhere"));

    private static string TempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(home);
        return home;
    }

    [Fact]
    public void Install_writes_the_shared_folder_and_links_claude()
    {
        var home = TempHome();
        try
        {
            Directory.CreateDirectory(Path.Combine(home, ".claude", "skills", "flextime"));
            File.WriteAllText(Path.Combine(home, ".claude", "skills", "flextime", "SKILL.md"), "old copy");

            var (path, link) = Skill.Install(home);

            Assert.Equal(Path.Combine(home, ".agents", "skills", "flextime", "SKILL.md"), path);
            Assert.Equal(Skill.Text, File.ReadAllText(path));
            Assert.Equal(Path.Combine(home, ".claude", "skills", "flextime"), link);
            // The old copy is gone; Claude reads the shared file.
            Assert.Equal(Skill.Text, File.ReadAllText(Path.Combine(link!, "SKILL.md")));

            // Again, over its own link.
            Assert.Equal(link, Skill.Install(home).Link);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Install_without_claude_makes_no_link()
    {
        var home = TempHome();
        try
        {
            Assert.Null(Skill.Install(home).Link);
            Assert.False(Directory.Exists(Path.Combine(home, ".claude")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void Install_keeps_what_the_user_put_beside_an_old_copy()
    {
        var home = TempHome();
        try
        {
            var claude = Path.Combine(home, ".claude", "skills", "flextime");
            Directory.CreateDirectory(claude);
            File.WriteAllText(Path.Combine(claude, "notes.md"), "mine");

            Assert.Null(Skill.Install(home).Link);
            Assert.Equal("mine", File.ReadAllText(Path.Combine(claude, "notes.md")));
            Assert.Equal(Skill.Text, File.ReadAllText(Path.Combine(claude, "SKILL.md")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
