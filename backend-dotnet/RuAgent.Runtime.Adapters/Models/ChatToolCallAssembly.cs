using System.Text;

namespace RuAgent.Runtime.Adapters.Models;

/// <summary>Reassembles Chat Completions calls by their response-local index.</summary>
internal sealed class ChatToolCallAssembly
{
    private const int MaximumCalls = 16;
    private const int MaximumArgumentsBytes = 64 * 1024;
    private readonly SortedDictionary<int, Call> _calls = [];
    private bool _unindexed;

    public void ObserveUnindexed()
    {
        if (_calls.Count > 0) throw Invalid();
        _unindexed = true;
    }

    public void Append(int index, string? id, string name, string arguments)
    {
        if (_unindexed || index is < 0 or >= MaximumCalls) throw Invalid();
        if (!_calls.TryGetValue(index, out var call))
        {
            call = new Call();
            _calls.Add(index, call);
        }
        if (id is not null)
        {
            if (id.Length is 0 or > 256 || call.Id is not null && call.Id != id
                || _calls.Any(pair => pair.Key != index && pair.Value.Id == id)) throw Invalid();
            call.Id = id;
        }
        var bytes = Encoding.UTF8.GetByteCount(arguments);
        if (call.Name.Length + name.Length > 128
            || bytes > MaximumArgumentsBytes - call.ArgumentsBytes
            || name.Contains('\0') || arguments.Contains('\0')) throw Invalid();
        call.Name.Append(name);
        call.Arguments.Append(arguments);
        call.ArgumentsBytes += bytes;
    }

    public List<ModelToolFragment> Complete() => _calls.Values.Select(call =>
        // The index already disambiguates calls even if this gateway omits ids.
        new ModelToolFragment(call.Id ??= $"ru_chat_{Guid.NewGuid():N}",
            call.Name.ToString(), call.Arguments.ToString())).ToList();

    private static ModelProviderException Invalid() => new("malformed_payload", retryable: false);

    private sealed class Call
    {
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
        public int ArgumentsBytes { get; set; }
    }
}
