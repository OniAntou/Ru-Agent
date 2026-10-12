namespace RuApp.Services;

/// <summary>UI-thread owner for completions queued before replacement or shutdown.</summary>
internal sealed class UiRequestLifetime
{
    private long _version;
    internal long Replace() => ++_version;
    internal bool Owns(long version) => version == _version;
    internal Action Bind(long version, Action apply) => () =>
    {
        if (Owns(version)) apply();
    };
}
