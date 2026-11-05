using RuAgent.Client;

namespace RuAgent.Cli.Tests;

internal static class ChatTestSession
{
    internal static readonly PublicHostSession Value =
        new(new Uri("http://127.0.0.1:1/"), "synthetic-token", null!);
}
