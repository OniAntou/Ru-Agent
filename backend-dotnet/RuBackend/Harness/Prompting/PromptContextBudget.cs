using System.Text.Json;
using RuAgent.Harness.Core.Prompting;

namespace RuBackend.Harness.Prompting;

internal static class PromptContextBudget
{
    public static SystemPromptContext Create(int version, string profile, string preset, PromptEnvironment environment,
        IReadOnlyList<PromptInstructionBlock> instructions, IReadOnlyList<PromptCapability> capabilities,
        PromptRuntimeContext runtime)
    {
        var empty = new SystemPromptContext(version, profile, preset, environment, instructions, capabilities,
            new PromptRuntimeContext(runtime.Mood, runtime.Hardware, []));
        // Reserve envelope/omission/rendering overhead. Mandatory policy and instructions
        // never disappear to make room for untrusted tool output.
        var available = SystemPromptContext.MaxSerializedBytes - empty.SerializeUtf8().Length - 16 * 1024;
        if (runtime.References.Sum(Measure) <= available)
            return new SystemPromptContext(version, profile, preset, environment, instructions, capabilities, runtime);
        var retained = new List<PromptReference>();
        var omitted = 0;
        for (var index = runtime.References.Count - 1; index >= 0; index--)
        {
            var reference = runtime.References[index];
            var size = Measure(reference);
            if (size > available || retained.Count >= PromptRuntimeContext.MaxRuntimeReferences - 1)
            {
                omitted++;
                continue;
            }
            retained.Add(reference);
            available -= size;
        }
        retained.Reverse();
        if (omitted > 0)
            retained.Insert(0, new PromptReference("references_omitted", "aggregate context budget",
                $"{omitted} older reference(s) omitted to fit the serialized context budget. Durable receipts remain stored; use available tool cursors or read the source again if needed."));
        return new SystemPromptContext(version, profile, preset, environment, instructions, capabilities,
            new PromptRuntimeContext(runtime.Mood, runtime.Hardware, retained));
    }

    private static int Measure(PromptReference reference) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        kind = reference.Kind, label = reference.Label, content = reference.Content,
    }).Length + 1;
}
