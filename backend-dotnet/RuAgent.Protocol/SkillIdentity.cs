namespace RuAgent.Protocol;

public static class SkillIdentity
{
    public static bool IsValid(string? id) => SkillMetadataParser.IsValidId(id);
}
