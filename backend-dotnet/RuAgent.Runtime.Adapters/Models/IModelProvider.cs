namespace RuAgent.Runtime.Adapters.Models;

public interface IModelProvider
{
    IAsyncEnumerable<ModelDelta> GenerateAsync(ModelRequest request, CancellationToken cancellationToken);
}

public interface IModelRequestBodyProjector
{
    string ProjectRequestBody(ModelRequest request);
}
