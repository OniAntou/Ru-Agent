using System.Globalization;
using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core;

public static class AgentPermissionPresetMapper
{
    public const string PlanWireValue = AgentPolicyProfileCatalog.PlanPresetWireValue;
    public const string StandardWireValue = AgentPolicyProfileCatalog.StandardPresetWireValue;
    public const string FullAccessWireValue = AgentPolicyProfileCatalog.FullAccessPresetWireValue;
    public static readonly int CurrentPermissionProfileVersion =
        int.Parse(AgentPolicyProfileCatalog.Version, CultureInfo.InvariantCulture);

    public static string ToWireValue(AgentPermissionPreset preset) => preset switch
    {
        AgentPermissionPreset.Plan => PlanWireValue,
        AgentPermissionPreset.Standard => StandardWireValue,
        AgentPermissionPreset.FullAccess => FullAccessWireValue,
        _ => throw new ArgumentOutOfRangeException(nameof(preset)),
    };

    public static bool TryParseWireValue(
        string? wireValue,
        out AgentPermissionPreset preset)
    {
        switch (wireValue)
        {
            case PlanWireValue:
                preset = AgentPermissionPreset.Plan;
                return true;
            case StandardWireValue:
                preset = AgentPermissionPreset.Standard;
                return true;
            case FullAccessWireValue:
                preset = AgentPermissionPreset.FullAccess;
                return true;
            default:
                preset = default;
                return false;
        }
    }
}
