using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Providers;

/// <summary>
/// The output cap (<c>max_tokens</c> / <c>max_completion_tokens</c>) a new run pin reserves.
/// </summary>
/// <remarks>
/// Every run used to be capped at 8,192 output tokens. Reasoning draws from the same
/// budget on every protocol, and a single <c>write_file</c> of a large file is one tool
/// call, so the cap cut off reasoning models mid-call: the truncated step was retracted
/// and redone, paying a whole extra model round trip. A model whose catalog (registry or
/// Models.dev <c>limit.output</c>) publishes its output limit now reserves up to
/// <see cref="Default"/> of it. Providers may reject a request above a model's own
/// limit, so an unpublished limit keeps the old 8,192. The reserve also comes out of the
/// input budget, so it is lifted only for a known context window and held to a quarter
/// of it, never below what runs had before.
/// </remarks>
public static class ModelOutputBudget
{
    /// <summary>The reserve when the model allows at least this much.</summary>
    public const int Default = 32_000;

    /// <summary>The reserve when the model's limit is not published.</summary>
    public const int Unpublished = 8_192;

    public static int For(int? modelOutputLimit, long? contextWindowTokens)
    {
        if (modelOutputLimit is not > 0) return Unpublished;
        var previous = Math.Min(modelOutputLimit.Value, Unpublished);
        // Without a known window the Host budgets input against a conservative default,
        // and a larger reserve would leave the first request no room at all.
        if (contextWindowTokens is not > 0) return previous;
        var quarter = (int)Math.Min(int.MaxValue, contextWindowTokens.Value / 4);
        var budget = Math.Min(Math.Min(modelOutputLimit.Value, Default), Math.Max(quarter, previous));

        // Default is far below the Messages ceiling; the clamp keeps that true if it grows.
        return Math.Clamp(budget, 1, AnthropicMessagesModelProvider.MaximumSupportedOutputTokens);
    }
}
