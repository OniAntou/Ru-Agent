using RuAgent.Client;

namespace RuApp.Models;

/// <summary>One skill as the Customize page lists it.</summary>
public sealed class CustomizeSkillRow
{
    public CustomizeSkillRow(SkillCatalogItem skill)
    {
        Skill = skill ?? throw new ArgumentNullException(nameof(skill));
    }

    public SkillCatalogItem Skill { get; }

    public string Id => Skill.Id;

    public string Name => string.IsNullOrWhiteSpace(Skill.Name) ? Skill.Id : Skill.Name;

    public string Description => Skill.Description;

    /// <summary>The command that runs it and where it was found.</summary>
    public string Origin => $"/skill {Skill.Id}  ·  {Skill.SourcePath}";

    public bool Enabled => Skill.Enabled;

    /// <summary>Whether the user may start it from chat; disabled skills cannot run.</summary>
    public bool CanUse => Skill.Enabled && Skill.UserInvocable;

    public string InvocationLabel => Skill.ModelInvocable ? "Ru may use it automatically" : "Runs only when you ask";
}
