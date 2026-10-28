using System.Diagnostics;
using System.Text;
using RuAgent.Harness.Core.Prompting;
using RuAgent.Protocol;

namespace RuAgent.Runtime.Model;

public sealed partial class ModelStepService
{
    private sealed class DeltaHoldback
    {
        private const string ToolCallOpenTag = "<tool_call>";
        private const string ToolCallCloseTag = "</tool_call>";
        private const int TagOverlapChars = 10;

        private readonly Func<string, ValueTask>? _sink;
        private readonly List<string> _held = [];
        private bool _decided;
        private bool _structured;
        private long _bytes;
        private string _tail = string.Empty;
        private bool _tailActive;
        private string _recent = string.Empty;

        public DeltaHoldback(Func<string, ValueTask>? sink)
        {
            _sink = sink;
        }

        public async ValueTask ForwardAsync(string delta)
        {
            if (_sink is null || delta.Length == 0)
            {
                return;
            }

            _bytes += Encoding.UTF8.GetByteCount(delta);
            if (_bytes > 1_000_000)
            {
                throw new ModelStepException("protocol_failure", retryable: false);
            }

            var overlap = _recent;
            _recent = TailOverlap(_recent + delta);

            if (_decided)
            {
                if (!_structured)
                {
                    await ForwardProseAsync(delta, overlap).ConfigureAwait(false);
                }

                return;
            }

            _held.Add(delta);
            var combined = string.Concat(_held);
            if (string.IsNullOrWhiteSpace(combined))
            {
                return;
            }

            var trimmed = combined.TrimStart();
            if (CouldBeStructuredSelection(trimmed))
            {
                _structured = true;
                return;
            }

            _decided = true;
            var tagAt = combined.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
            if (tagAt < 0)
            {
                foreach (var held in _held)
                {
                    await _sink(held).ConfigureAwait(false);
                }
            }
            else
            {
                // Prose arrived together with a later tool-call candidate in one
                // chunk: show only the prose, hold the candidate for end-of-step
                // validation like the streaming path does.
                if (tagAt > 0)
                {
                    await _sink(combined[..tagAt]).ConfigureAwait(false);
                }

                _tail = combined[tagAt..];
                _tailActive = true;
            }

            _held.Clear();
        }

        public async ValueTask ReleaseAsync()
        {
            if (_sink is not null)
            {
                if (_tailActive)
                {
                    // End of step with a held candidate tail. A complete
                    // trailing block is either valid (the final parse executed
                    // it, so this is unreachable) or invalid (drop it to match
                    // the stripped final text). An unterminated tail is
                    // ordinary prose: flush it.
                    if (!ModelToolSelectionParser.TrySplitTrailingBlock(_tail, out _, out _))
                    {
                        await _sink(_tail).ConfigureAwait(false);
                    }

                    _tail = string.Empty;
                    _tailActive = false;
                }
                else if (_structured)
                {
                    foreach (var held in _held)
                    {
                        await _sink(held).ConfigureAwait(false);
                    }
                }
            }

            _held.Clear();
            _decided = true;
            _structured = false;
        }

        private async ValueTask ForwardProseAsync(string delta, string overlap)
        {
            if (!_tailActive)
            {
                var scan = overlap + delta;
                var tagAt = scan.IndexOf(ToolCallOpenTag, StringComparison.OrdinalIgnoreCase);
                if (tagAt < 0)
                {
                    await _sink!(delta).ConfigureAwait(false);
                    return;
                }

                // A tool-call candidate starts mid-stream (possibly split
                // across deltas, reconstructed via overlap): show the prose
                // before it and hold the candidate for end-of-step validation.
                var sinkCount = tagAt - overlap.Length;
                if (sinkCount > 0)
                {
                    await _sink!(delta[..sinkCount]).ConfigureAwait(false);
                }

                _tail = scan[tagAt..];
                _tailActive = true;
                return;
            }

            _tail += delta;
            // A candidate stops being trailing as soon as real prose follows
            // its closing tag: flush everything, it was discussion, not a call.
            var closeAt = _tail.LastIndexOf(ToolCallCloseTag, StringComparison.OrdinalIgnoreCase);
            if (closeAt >= 0
                && !string.IsNullOrWhiteSpace(_tail[(closeAt + ToolCallCloseTag.Length)..]))
            {
                await _sink!(_tail).ConfigureAwait(false);
                _tail = string.Empty;
                _tailActive = false;
            }
        }

        private static string TailOverlap(string value) =>
            value.Length <= TagOverlapChars ? value : value[^TagOverlapChars..];

        private static bool CouldBeStructuredSelection(string candidate)
        {
            if (candidate[0] is '{' or '[')
            {
                return true;
            }

            const string toolCallTag = "<tool_call>";
            if (candidate[0] == '<'
                && (toolCallTag.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(toolCallTag, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            // A fenced block can carry the tool envelope (```json ... ```), which the
            // final parse unwraps and executes. Hold it for end-of-step validation
            // like other structured candidates so raw tool JSON is never streamed
            // as chat text. Partial fences (`/``) are held too; prose starting
            // with a single backtick plus text still streams immediately.
            if (candidate[0] == '`')
            {
                if (candidate.Length < 3 && candidate.Trim('`').Length == 0)
                {
                    return true;
                }

                if (candidate.StartsWith("```", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
