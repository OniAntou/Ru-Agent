namespace RuBackend.Configuration;

public sealed class BackendOptions
{
    public const string SectionName = "RuBackend";

    public string BindUrl { get; set; } = "http://127.0.0.1:8010";

    public string DesktopToken { get; set; } = string.Empty;

    public long MaxRequestBodyBytes { get; set; } = 8 * 1024 * 1024;
}
