using System;
using System.Collections.Generic;

namespace RuApp.Services;

/// <summary>
/// Bounds for the inline question panel. The question and its options arrive from
/// the wire and the answer leaves to the resolve route, so each side is clipped
/// before it reaches a control or the request body.
/// </summary>
public static class AgentQuestionPresentation
{
    /// <summary>Longest question the panel renders before truncating.</summary>
    public const int MaximumQuestionLength = 2000;

    /// <summary>Longest option label the panel renders before truncating.</summary>
    public const int MaximumOptionLength = 200;

    /// <summary>Most options the panel offers; the rest stay hidden.</summary>
    public const int MaximumOptions = 8;

    /// <summary>The resolve route accepts answers from 1 to 8192 characters.</summary>
    public const int MaximumAnswerLength = 8192;

    public const string FallbackQuestion = "The agent is asking for your input.";

    /// <summary>Collapses newlines and truncates so the panel cannot be grown by a frame.</summary>
    public static string RenderQuestion(string? question)
    {
        string bounded = AgentApprovalSummaryPresentation.Bounded(
            question ?? string.Empty,
            MaximumQuestionLength);
        return bounded.Length == 0 ? FallbackQuestion : bounded;
    }

    /// <summary>
    /// The option labels the panel may show: blank labels are dropped and the
    /// list is capped, so a hostile frame cannot fill the card with buttons.
    /// </summary>
    public static IReadOnlyList<string> RenderOptions(IReadOnlyList<string>? options)
    {
        if (options is null || options.Count == 0)
        {
            return Array.Empty<string>();
        }

        var rendered = new List<string>(Math.Min(options.Count, MaximumOptions));
        foreach (var option in options)
        {
            if (rendered.Count >= MaximumOptions)
            {
                break;
            }

            string bounded = AgentApprovalSummaryPresentation.Bounded(option, MaximumOptionLength);
            if (bounded.Length > 0)
            {
                rendered.Add(bounded);
            }
        }

        return rendered;
    }

    /// <summary>Whether the answer is within the resolve route's contract.</summary>
    public static bool IsAnswerWithinBounds(string? answer) =>
        !string.IsNullOrEmpty(answer) && answer.Length <= MaximumAnswerLength;
}
