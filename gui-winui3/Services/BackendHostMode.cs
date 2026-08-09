namespace RuApp.Services;

public enum BackendHostMode
{
    FastApi,
    AspNetCore,
}

public static class BackendHostModeParser
{
    public static BackendHostMode Parse(string? raw)
    {
        return raw?.Trim().ToLowerInvariant() switch
        {
            null or "" => BackendHostMode.AspNetCore,
            "fastapi" => BackendHostMode.FastApi,
            "aspnetcore" => BackendHostMode.AspNetCore,
            _ => throw new ArgumentException(
                "RU_BACKEND_HOST_MODE must be 'fastapi' or 'aspnetcore'.",
                nameof(raw)),
        };
    }
}
