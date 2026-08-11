namespace RuBackend.Configuration;

public sealed class BackendOptions
{
    public const string SectionName = "RuBackend";

    public string BindUrl { get; set; } = "http://127.0.0.1:8010";

    public string UpstreamUrl { get; set; } = "http://127.0.0.1:8000";

    public string DesktopToken { get; set; } = string.Empty;

    public long MaxRequestBodyBytes { get; set; } = 8 * 1024 * 1024;

    public BackendOwnershipOptions Ownership { get; set; } = new();
}

public sealed class BackendOwnershipOptions
{
    public string Settings { get; set; } = "python";

    public string Sessions { get; set; } = "python";

    public string Activity { get; set; } = "python";

    public string Memory { get; set; } = "python";

    public string Stt { get; set; } = "python";

    public string Vts { get; set; } = "python";
}
