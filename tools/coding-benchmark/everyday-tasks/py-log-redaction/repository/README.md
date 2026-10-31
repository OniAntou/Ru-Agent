# Structured log sanitization

The logger accepts nested records assembled by integrations. Sanitization must
preserve useful non-secret context while returning a detached data structure.
