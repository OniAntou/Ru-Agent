using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Privacy;
using RuBackend.Conversation;

namespace RuBackend.Harness;

/// <summary>
/// Keeps private reply markers out of transient Agent frames while retaining the
/// existing split-secret redaction boundary.
/// </summary>
public sealed class AgentReplyTransientRedactor : ITransientContentRedactor
{
    private readonly KnownSecretRedactor _secrets;
    private readonly IStreamSanitizer _markers;

    public AgentReplyTransientRedactor(
        KnownSecretRedactor secrets,
        IStreamSanitizer markers)
    {
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _markers = markers ?? throw new ArgumentNullException(nameof(markers));
    }

    public ITransientContentRedactionSession CreateSession() =>
        new Session(_secrets.CreateSession(), _markers);

    private sealed class Session : ITransientContentRedactionSession
    {
        private readonly KnownSecretRedactionSession _secrets;
        private readonly IStreamSanitizer _markers;
        private string _raw = string.Empty;
        private string _visible = string.Empty;
        private bool _completed;

        public Session(
            KnownSecretRedactionSession secrets,
            IStreamSanitizer markers)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _markers = markers ?? throw new ArgumentNullException(nameof(markers));
        }

        public string Append(string content)
        {
            ArgumentNullException.ThrowIfNull(content);
            if (_completed)
            {
                throw new InvalidOperationException("redaction_session_completed");
            }

            _raw = _markers.Append(_raw, content);
            var nextVisible = _markers.Visible(_raw);
            var delta = VisibleSuffix(nextVisible);
            _visible = nextVisible;
            return _secrets.Append(delta);
        }

        public string Complete()
        {
            if (_completed)
            {
                return string.Empty;
            }

            _completed = true;
            var nextVisible = _markers.Visible(_raw);
            var delta = VisibleSuffix(nextVisible);
            _visible = nextVisible;
            return _secrets.Append(delta) + _secrets.Complete();
        }

        private string VisibleSuffix(string nextVisible)
        {
            if (nextVisible.StartsWith(_visible, StringComparison.Ordinal))
            {
                return nextVisible[_visible.Length..];
            }

            // Private markers are terminal metadata. If an upstream model violates
            // that contract and rewrites visible text, let final payload reconciliation
            // correct the UI instead of emitting duplicated content.
            return string.Empty;
        }
    }
}
