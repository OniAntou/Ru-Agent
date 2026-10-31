using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SkillRootsTests
{
    [Fact]
    public void Lists_workspace_roots_first_then_the_configured_and_per_user_roots()
    {
        var roots = SkillRoots.For("w", localApplicationDataRoot: "local", userProfileRoot: "home", configuredRoot: "extra");

        Assert.Equal(
            new[]
            {
                Path.Combine("w", "skills"),
                Path.Combine("w", ".ru", "skills"),
                Path.Combine("w", ".agents", "skills"),
                "extra",
                Path.Combine("home", ".ru", "skills"),
                Path.Combine("home", ".agents", "skills"),
                Path.Combine("local", "Ru", "skills"),
            },
            roots);
    }

    [Fact]
    public void Leaves_out_the_roots_that_are_not_configured()
    {
        var roots = SkillRoots.For("w", localApplicationDataRoot: "", userProfileRoot: "", configuredRoot: " ");

        Assert.Equal(3, roots.Count);
        Assert.All(roots, root => Assert.StartsWith("w", root, StringComparison.Ordinal));
    }

    [Fact]
    public void User_installation_root_is_the_existing_local_application_data_skill_root()
    {
        Assert.Equal(Path.Combine("local", "Ru", "skills"), SkillRoots.UserInstallationRoot("local"));
    }
}
