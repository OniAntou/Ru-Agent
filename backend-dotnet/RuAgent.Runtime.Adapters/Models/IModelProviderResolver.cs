namespace RuAgent.Runtime.Adapters.Models;

public interface IModelProviderResolver
{
    IModelProvider Resolve(string protocol);
}
