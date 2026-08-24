using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core.Context;

public interface IModelContextSource
{
    Task<ModelContextSourceSnapshot> LoadAsync(
        RunId runId,
        CancellationToken cancellationToken);
}

public sealed record ModelContextSourceSnapshot
{
    public ModelContextSourceSnapshot(
        IReadOnlyList<ModelContextEntry> sessionBase,
        IReadOnlyList<ModelContextEntry> runLocal)
    {
        ArgumentNullException.ThrowIfNull(sessionBase);
        ArgumentNullException.ThrowIfNull(runLocal);
        SessionBase = Copy(sessionBase, nameof(sessionBase));
        RunLocal = Copy(runLocal, nameof(runLocal));
    }

    public IReadOnlyList<ModelContextEntry> SessionBase { get; }

    public IReadOnlyList<ModelContextEntry> RunLocal { get; }

    private static IReadOnlyList<ModelContextEntry> Copy(
        IReadOnlyList<ModelContextEntry> source,
        string parameterName)
    {
        var copy = source.ToArray();
        if (copy.Any(entry => entry is null))
        {
            throw new ArgumentException("Context entries cannot contain null.", parameterName);
        }

        return new ReadOnlyCollection<ModelContextEntry>(copy);
    }
}
