namespace RuAgent.Harness.Core;

/// <summary>
/// The proposals of a model step parked on an <c>ask_user</c> call. Unlike an
/// approval wait there is no stash: the answer route completes the asked call
/// durably, so resume only needs the proposals the durable stream can rebuild.
/// </summary>
public sealed record RetainedInputStep(
    IReadOnlyList<ToolProposal> Proposals,
    InputId InputId,
    ModelStepId ModelStepId);
