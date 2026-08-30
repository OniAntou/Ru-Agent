using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Devices;

/// <summary>Validated VTube Studio motion settings shared by HTTP state handlers.</summary>
internal sealed record VtubeStudioMotionConfig(
    double SwayAmplitudeIdle,
    double SwayAmplitudeSpeaking,
    double HeadScale,
    double EyeScale,
    double MicroActionMinDelay,
    double MicroActionMaxDelay,
    double PoseMinInterval,
    double PoseMaxInterval,
    double BreathAmplitude,
    double BreathRate)
{
    private static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "sway_amplitude_idle", "sway_amplitude_speaking", "head_scale", "eye_scale",
        "micro_action_min_delay", "micro_action_max_delay", "pose_min_interval",
        "pose_max_interval", "breath_amplitude", "breath_rate",
    };

    public static VtubeStudioMotionConfig Defaults => new(10, 16, 100, 100, 8, 22, 90, 130, 1.5, 0.15);

    public JsonObject ToJson() => new()
    {
        ["sway_amplitude_idle"] = SwayAmplitudeIdle,
        ["sway_amplitude_speaking"] = SwayAmplitudeSpeaking,
        ["head_scale"] = HeadScale,
        ["eye_scale"] = EyeScale,
        ["micro_action_min_delay"] = MicroActionMinDelay,
        ["micro_action_max_delay"] = MicroActionMaxDelay,
        ["pose_min_interval"] = PoseMinInterval,
        ["pose_max_interval"] = PoseMaxInterval,
        ["breath_amplitude"] = BreathAmplitude,
        ["breath_rate"] = BreathRate,
    };

    public static VtubeStudioMotionConfig Parse(
        JsonObject payload,
        VtubeStudioMotionConfig previous)
    {
        if (payload.Any(property => !AllowedFields.Contains(property.Key)))
        {
            throw new ArgumentException("Unknown VTS configuration field.", nameof(payload));
        }

        var values = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["sway_amplitude_idle"] = Read(payload, "sway_amplitude_idle", previous.SwayAmplitudeIdle, 5, 30),
            ["sway_amplitude_speaking"] = Read(payload, "sway_amplitude_speaking", previous.SwayAmplitudeSpeaking, 5, 40),
            ["head_scale"] = Read(payload, "head_scale", previous.HeadScale, 0, 200),
            ["eye_scale"] = Read(payload, "eye_scale", previous.EyeScale, 0, 200),
            ["micro_action_min_delay"] = Read(payload, "micro_action_min_delay", previous.MicroActionMinDelay, 1, 120),
            ["micro_action_max_delay"] = Read(payload, "micro_action_max_delay", previous.MicroActionMaxDelay, 1, 120),
            ["pose_min_interval"] = Read(payload, "pose_min_interval", previous.PoseMinInterval, 10, 600),
            ["pose_max_interval"] = Read(payload, "pose_max_interval", previous.PoseMaxInterval, 10, 600),
            ["breath_amplitude"] = Read(payload, "breath_amplitude", previous.BreathAmplitude, 0, 5),
            ["breath_rate"] = Read(payload, "breath_rate", previous.BreathRate, 0.05, 0.5),
        };
        if (values["micro_action_max_delay"] < values["micro_action_min_delay"]
            || values["pose_max_interval"] < values["pose_min_interval"])
        {
            throw new ArgumentException("VTS configuration interval order is invalid.", nameof(payload));
        }

        return new VtubeStudioMotionConfig(
            values["sway_amplitude_idle"], values["sway_amplitude_speaking"],
            values["head_scale"], values["eye_scale"],
            values["micro_action_min_delay"], values["micro_action_max_delay"],
            values["pose_min_interval"], values["pose_max_interval"],
            values["breath_amplitude"], values["breath_rate"]);
    }

    private static double Read(
        JsonObject payload,
        string key,
        double fallback,
        double minimum,
        double maximum)
    {
        if (!payload.TryGetPropertyValue(key, out var node))
        {
            return fallback;
        }

        if (node is JsonValue value
            && value.TryGetValue<double>(out var parsed)
            && double.IsFinite(parsed)
            && parsed >= minimum
            && parsed <= maximum)
        {
            return parsed;
        }

        throw new ArgumentException("VTS configuration value is invalid.", nameof(payload));
    }
}
