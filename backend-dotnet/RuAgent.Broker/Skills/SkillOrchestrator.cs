using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Skills;

public interface IToolInvoker
{
    Task<ToolResult> ExecuteAsync(
        string sessionId,
        ToolManifest manifest,
        JsonObject arguments,
        int deadlineMs,
        CancellationToken cancellationToken);
}

public sealed class SkillOrchestrator
{
    public const int MaxOutputBytes = 512 * 1024;
    private const int MaxSummaryChars = 512;
    private const int MaxBranchVisits = 64;

    private readonly ToolRegistry _tools;
    private readonly ToolPolicyEngine _policy;
    private readonly IToolInvoker _invoker;

    public SkillOrchestrator(ToolRegistry tools, ToolPolicyEngine policy, IToolInvoker invoker)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public async Task<SkillRun> RunAsync(
        string sessionId,
        SkillManifest skill,
        PolicyGrant grant,
        JsonObject input,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(input);

        var runId = $"skill-{Guid.NewGuid():N}";
        var receipts = new List<SkillStepReceipt>();
        var started = Stopwatch.StartNew();
        var grantDecision = _policy.EvaluateSkill(grant, skill.Id, skill.RequiredScopes);
        if (!grantDecision.Allowed)
        {
            return Failed(runId, sessionId, skill.Id, 0, grantDecision.Code, "Skill is not granted.", receipts);
        }

        try
        {
            ToolSchemaValidator.Validate(skill.InputSchema, input, "schema_invalid");
        }
        catch (ToolSchemaException)
        {
            return Failed(runId, sessionId, skill.Id, 0, "schema_invalid", "Skill input was rejected.", receipts);
        }

        using var timeout = new CancellationTokenSource(skill.TimeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var token = linked.Token;
        JsonNode? current = input.DeepClone();
        var index = 0;
        var executed = 0;
        var branchVisits = 0;
        var lastSummary = "Skill completed.";
        var maxSteps = Math.Min(skill.MaxSteps, 32);

        try
        {
            while (index >= 0 && index < skill.Steps.Count)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Cancelled(runId, sessionId, skill.Id, index, receipts);
                }

                if (timeout.IsCancellationRequested)
                {
                    return Failed(runId, sessionId, skill.Id, index, "timed_out", "Skill timed out.", receipts);
                }

                if (executed >= maxSteps)
                {
                    return Failed(runId, sessionId, skill.Id, index, "max_steps_exceeded", "Skill step limit reached.", receipts);
                }

                var step = skill.Steps[index];
                var stepIndex = index;
                executed++;

                switch (step.Kind)
                {
                    case SkillStepKind.CallTool:
                    {
                        if (string.IsNullOrWhiteSpace(step.ToolId))
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "invalid_skill_step", "Skill step is invalid.", receipts);
                        }

                        var manifest = ResolveTool(step);
                        if (manifest is null)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "skill_tool_not_registered", "Skill tool is unavailable.", receipts);
                        }

                        var decision = _policy.Evaluate(grant, manifest);
                        if (!decision.Allowed)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, decision.Code, "Skill tool scope is denied.", receipts, step);
                        }

                        if (decision.RequiresApproval || skill.RequiresApproval)
                        {
                            return new SkillRun(
                                runId,
                                sessionId,
                                skill.Id,
                                SkillRunState.ApprovalRequired,
                                stepIndex,
                                "Approval is required.",
                                "approval_required",
                                null,
                                receipts.Append(new SkillStepReceipt(
                                    stepIndex,
                                    step.Kind,
                                    step.Name,
                                    manifest.Id,
                                    ToolResultStatuses.ApprovalRequired,
                                    "Approval is required.")).ToArray());
                        }

                        JsonObject arguments;
                        try
                        {
                            arguments = BindArguments(step.Arguments, input, current);
                            ToolSchemaValidator.Validate(manifest.InputSchema, arguments, "schema_invalid");
                        }
                        catch (ToolSchemaException)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "schema_invalid", "Skill tool arguments were rejected.", receipts, step);
                        }

                        ToolResult result;
                        try
                        {
                            result = await _invoker.ExecuteAsync(
                                sessionId,
                                manifest,
                                arguments,
                                Math.Min(manifest.TimeoutMs, skill.TimeoutMs),
                                token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            return Cancelled(runId, sessionId, skill.Id, stepIndex, receipts);
                        }
                        catch (OperationCanceledException)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "timed_out", "Skill timed out.", receipts, step);
                        }

                        if (!ToolResultStatuses.Known.Contains(result.Status))
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "invalid_tool_result", "Skill tool returned an invalid result.", receipts, step);
                        }

                        var receipt = new SkillStepReceipt(
                            stepIndex,
                            step.Kind,
                            step.Name,
                            manifest.Id,
                            result.Status,
                            ClampSummary(result.SafeSummary));
                        if (result.Status == ToolResultStatuses.Cancelled)
                        {
                            return new SkillRun(runId, sessionId, skill.Id, SkillRunState.Cancelled, stepIndex, "Skill cancelled.", "cancelled", null, receipts.Append(receipt).ToArray());
                        }

                        if (result.Status == ToolResultStatuses.ApprovalRequired)
                        {
                            return new SkillRun(runId, sessionId, skill.Id, SkillRunState.ApprovalRequired, stepIndex, "Approval is required.", "approval_required", null, receipts.Append(receipt).ToArray());
                        }

                        if (result.Status is ToolResultStatuses.Denied or ToolResultStatuses.Failed or ToolResultStatuses.TimedOut)
                        {
                            return Failed(
                                runId,
                                sessionId,
                                skill.Id,
                                stepIndex,
                                result.ErrorCode ?? "skill_tool_failed",
                                "Skill tool failed.",
                                receipts,
                                step,
                                receipt);
                        }

                        current = result.Output.DeepClone();
                        lastSummary = ClampSummary(result.SafeSummary);
                        receipts.Add(receipt);
                        if (!WithinOutputCap(current))
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "skill_output_too_large", "Skill output exceeded its limit.", receipts);
                        }

                        index++;
                        break;
                    }
                    case SkillStepKind.ValidateResult:
                    {
                        var valid = SkillStepEvaluator.Evaluate(step.Name, current, step.Arguments, out var predicateError);
                        var receipt = new SkillStepReceipt(
                            stepIndex,
                            step.Kind,
                            step.Name,
                            null,
                            valid ? ToolResultStatuses.Succeeded : ToolResultStatuses.Failed,
                            valid ? "Result validated." : "Result predicate was false.");
                        if (predicateError is not null)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, predicateError, "Skill predicate was rejected.", receipts, step, receipt);
                        }

                        if (!valid)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "result_invalid", "Skill result did not satisfy its predicate.", receipts, step, receipt);
                        }

                        receipts.Add(receipt);
                        index++;
                        break;
                    }
                    case SkillStepKind.Branch:
                    {
                        if (++branchVisits > MaxBranchVisits)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "branch_limit_exceeded", "Skill branch limit reached.", receipts, step);
                        }

                        var predicate = step.BranchOn ?? step.Name;
                        var selected = SkillStepEvaluator.Evaluate(predicate, current, step.Arguments, out var predicateError);
                        var next = selected ? step.TrueIndex : step.FalseIndex;
                        var receipt = new SkillStepReceipt(
                            stepIndex,
                            step.Kind,
                            step.Name,
                            null,
                            ToolResultStatuses.Succeeded,
                            selected ? "True branch selected." : "False branch selected.");
                        if (predicateError is not null || next is null || next.Value < 0 || next.Value >= skill.Steps.Count)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, predicateError ?? "invalid_skill_branch", "Skill branch was rejected.", receipts, step, receipt);
                        }

                        receipts.Add(receipt);
                        index = next.Value;
                        break;
                    }
                    case SkillStepKind.NamedTransform:
                    {
                        var transformName = step.Transform ?? step.Name;
                        var transformed = SkillNamedTransformRegistry.Apply(transformName, current, step.Arguments, out var transformError);
                        var receipt = new SkillStepReceipt(
                            stepIndex,
                            step.Kind,
                            step.Name,
                            null,
                            transformError is null ? ToolResultStatuses.Succeeded : ToolResultStatuses.Failed,
                            transformError is null ? "Result transformed." : "Skill transform was rejected.");
                        if (transformError is not null || transformed is null)
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, transformError ?? "invalid_skill_transform", "Skill transform was rejected.", receipts, step, receipt);
                        }

                        current = transformed;
                        if (!WithinOutputCap(current))
                        {
                            return Failed(runId, sessionId, skill.Id, stepIndex, "skill_output_too_large", "Skill output exceeded its limit.", receipts, step, receipt);
                        }

                        receipts.Add(receipt);
                        index++;
                        break;
                    }
                    default:
                        return Failed(runId, sessionId, skill.Id, stepIndex, "invalid_skill_step", "Skill step is unsupported.", receipts, step);
                }
            }

            var output = current as JsonObject ?? new JsonObject { ["result"] = current?.DeepClone() };
            try
            {
                ToolSchemaValidator.Validate(skill.OutputSchema, output, "schema_invalid");
            }
            catch (ToolSchemaException)
            {
                return Failed(runId, sessionId, skill.Id, Math.Max(0, index - 1), "schema_invalid", "Skill output was rejected.", receipts);
            }

            return new SkillRun(
                runId,
                sessionId,
                skill.Id,
                SkillRunState.Completed,
                receipts.Count == 0 ? 0 : receipts[^1].StepIndex,
                lastSummary,
                null,
                output,
                receipts.ToArray(),
                runId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Cancelled(runId, sessionId, skill.Id, index, receipts);
        }
        catch (OperationCanceledException)
        {
            return Failed(runId, sessionId, skill.Id, index, "timed_out", "Skill timed out.", receipts);
        }
    }

    private ToolManifest? ResolveTool(SkillStep step)
    {
        return _tools.List()
            .Where(tool => string.Equals(tool.Id, step.ToolId, StringComparison.Ordinal))
            .Where(tool => step.ToolVersion is null || string.Equals(tool.Version, step.ToolVersion, StringComparison.Ordinal))
            .FirstOrDefault();
    }

    private static JsonObject BindArguments(JsonObject? declared, JsonObject input, JsonNode? current)
    {
        if (declared is null || declared.Count == 0)
        {
            return input.DeepClone().AsObject();
        }

        var bound = BindNode(declared, input, current);
        return bound as JsonObject ?? new JsonObject();
    }

    private static JsonNode? BindNode(JsonNode? node, JsonObject input, JsonNode? current)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            var path = text.StartsWith("$input.", StringComparison.Ordinal)
                ? (input, text[7..])
                : text.StartsWith("$result.", StringComparison.Ordinal)
                    ? (current, text[8..])
                    : (null, string.Empty);
            if (path.Item1 is not null && TryGetPath(path.Item1, path.Item2, out var resolved))
            {
                return resolved?.DeepClone();
            }

            return value.DeepClone();
        }

        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj)
            {
                result[pair.Key] = BindNode(pair.Value, input, current);
            }

            return result;
        }

        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(item => BindNode(item, input, current)).ToArray());
        }

        return node?.DeepClone();
    }

    private static bool TryGetPath(JsonNode node, string path, out JsonNode? value)
    {
        value = node;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(part, out value))
            {
                value = null;
                return false;
            }
        }

        return true;
    }

    private static bool WithinOutputCap(JsonNode? value) =>
        value is null || Encoding.UTF8.GetByteCount(value.ToJsonString()) <= MaxOutputBytes;

    private static string ClampSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "Skill step completed.";
        }

        return summary.Length <= MaxSummaryChars ? summary : summary[..MaxSummaryChars];
    }

    private static SkillRun Failed(
        string runId,
        string sessionId,
        string skillId,
        int stepIndex,
        string errorCode,
        string summary,
        IReadOnlyList<SkillStepReceipt> receipts,
        SkillStep? step = null,
        SkillStepReceipt? receipt = null)
    {
        var next = receipts.ToList();
        if (receipt is not null)
        {
            next.Add(receipt);
        }

        return new SkillRun(
            runId,
            sessionId,
            skillId,
            SkillRunState.Failed,
            stepIndex,
            summary,
            errorCode,
            null,
            next,
            runId);
    }

    private static SkillRun Cancelled(
        string runId,
        string sessionId,
        string skillId,
        int stepIndex,
        IReadOnlyList<SkillStepReceipt> receipts) =>
        new(
            runId,
            sessionId,
            skillId,
            SkillRunState.Cancelled,
            stepIndex,
            "Skill cancelled.",
            "cancelled",
            null,
            receipts,
            runId);
}
