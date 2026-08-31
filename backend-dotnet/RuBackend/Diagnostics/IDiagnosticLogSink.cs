namespace RuBackend.Diagnostics;

public interface IDiagnosticLogSink
{
    ValueTask AppendAsync(string filePath, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}
