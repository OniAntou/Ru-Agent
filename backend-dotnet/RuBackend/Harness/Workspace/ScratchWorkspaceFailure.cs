namespace RuBackend.Harness.Workspace;

/// <summary>
/// Bounded, actionable copy for a scratch allocation that failed. The store's
/// synthetic codes say what went wrong without naming a path; the fallback names
/// the two things a user can actually change. Both the run-create path and the
/// pre-run workspace endpoint answer the same code, so the copy lives here once.
/// </summary>
public static class ScratchWorkspaceFailure
{
    public const string Code = "scratch_workspace_unavailable";

    public static string CopyFor(Exception error) => error.Message switch
    {
        "scratch_workspace_reparse" =>
            "The chat's workspace folder is a link or junction, so Ru will not write through it. Rename or delete that folder and send again.",
        "scratch_workspace_unowned" =>
            "Ru can no longer confirm that this chat owns its workspace folder, because its .ru-scratch.json marker was changed outside Ru. Rename or delete that folder and send again.",
        "scratch_workspace_unrecorded" =>
            "Ru created the chat's workspace folder but could not record that it belongs to this chat. Check free space and folder permissions, then send again.",
        _ =>
            "Ru could not prepare a workspace for this chat. Check free space and folder permissions, then send again.",
    };
}
