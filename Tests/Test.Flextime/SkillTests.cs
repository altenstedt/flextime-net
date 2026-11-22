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
}
