using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Prompting;

public sealed record PromptBlockDigest
{
    public PromptBlockDigest(string blockId, string sha256)
    {
        BlockId = PromptText.RequireIdentifier(blockId, 256, nameof(blockId), allowColon: true);
        Sha256 = PromptText.RequireDigest(sha256, nameof(sha256));
    }

    public string BlockId { get; }

    public string Sha256 { get; }
}

public sealed record SystemPromptReceipt
{
    public const string SupportedComposerVersion = "1.0.0";

    /// <summary>
    /// A deterministic receipt for the transitional empty context used by legacy
    /// direct-runner test fixtures. Production requests are always bound to a
    /// root-derived context before dispatch.
    /// </summary>
    public static SystemPromptReceipt Empty => new(
        SupportedComposerVersion,
        new string('0', 64),
        new[]
        {
            new PromptBlockDigest("ru.persona", new string('0', 64)),
            new PromptBlockDigest("agent.contract", new string('0', 64)),
            new PromptBlockDigest("environment", new string('0', 64)),
            new PromptBlockDigest("profile", new string('0', 64)),
            new PromptBlockDigest("capabilities", new string('0', 64)),
            new PromptBlockDigest("runtime", new string('0', 64)),
        });

    public SystemPromptReceipt(
        string composerVersion,
        string promptSha256,
        IReadOnlyList<PromptBlockDigest> blockDigests)
    {
        if (!string.Equals(composerVersion, SupportedComposerVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported prompt composer version.", nameof(composerVersion));
        }

        PromptSha256 = PromptText.RequireDigest(promptSha256, nameof(promptSha256));
        ArgumentNullException.ThrowIfNull(blockDigests);
        var copiedBlocks = blockDigests.ToArray();
        if (copiedBlocks.Any(block => block is null))
        {
            throw new ArgumentException("Prompt block digests cannot contain null.", nameof(blockDigests));
        }

        if (copiedBlocks.Length > 32)
        {
            throw new ArgumentException("Prompt block digests exceed the Core cap.", nameof(blockDigests));
        }

        var blockIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in copiedBlocks)
        {
            if (!blockIds.Add(block.BlockId))
            {
                throw new ArgumentException("Prompt block IDs must be unique.", nameof(blockDigests));
            }
        }

        ComposerVersion = composerVersion;
        BlockDigests = new ReadOnlyCollection<PromptBlockDigest>(copiedBlocks);
    }

    public string ComposerVersion { get; }

    public string PromptSha256 { get; }

    public IReadOnlyList<PromptBlockDigest> BlockDigests { get; }

    public void ValidateAgainst(SystemPromptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(ComposerVersion, SupportedComposerVersion, StringComparison.Ordinal)
            || !PromptHash.IsLowerHexSha256(PromptSha256))
        {
            throw new ArgumentException("Invalid prompt receipt version or digest.");
        }

        var expectedIds = new List<string>
        {
            "ru.persona",
            "agent.contract",
            "environment",
        };
        expectedIds.AddRange(context.InstructionBlocks.Select(item => $"instruction.{item.SourceId}"));
        expectedIds.Add("profile");
        expectedIds.Add("capabilities");
        expectedIds.Add("runtime");

        if (BlockDigests.Count != expectedIds.Count
            || !BlockDigests.Select(item => item.BlockId).SequenceEqual(expectedIds, StringComparer.Ordinal)
            || BlockDigests.Any(item => !PromptHash.IsLowerHexSha256(item.Sha256)))
        {
            throw new ArgumentException("Prompt receipt block order or digest is invalid.");
        }

        foreach (var instruction in context.InstructionBlocks)
        {
            var receiptBlock = BlockDigests.Single(item =>
                string.Equals(item.BlockId, $"instruction.{instruction.SourceId}", StringComparison.Ordinal));
            if (!string.Equals(receiptBlock.Sha256, instruction.ContentSha256, StringComparison.Ordinal))
            {
                throw new ArgumentException("Prompt receipt does not match instruction context.");
            }
        }
    }

    public void ValidateAgainst(SystemPromptContext context, string text)
    {
        ValidateAgainst(context);
        ArgumentNullException.ThrowIfNull(text);
        if (!string.Equals(PromptHash.Sha256(text), PromptSha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("Prompt receipt does not match rendered prompt text.", nameof(text));
        }
    }
}
