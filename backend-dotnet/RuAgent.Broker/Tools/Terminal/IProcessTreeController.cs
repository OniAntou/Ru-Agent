using System.Diagnostics;

namespace RuAgent.Broker.Tools.Terminal;

[Flags]
public enum JobLimitFlags : uint
{
    None = 0x00000000,
    KillOnJobClose = 0x00002000,
    SilentBreakawayOk = 0x00000800,
    BreakawayOk = 0x00001000,
    DieOnUnhandledException = 0x00000400,
}

/// <summary>
/// Owns one Windows Job Object for a single terminal call. Every process spawned
/// inside the job — including grandchildren — dies when the job closes or is
/// explicitly killed; breakaway attempts are never permitted.
/// </summary>
public interface IProcessTreeController : IDisposable
{
    void Assign(int processId);

    bool KillTree();
}
