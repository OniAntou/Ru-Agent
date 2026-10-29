namespace RepositoryLedger;

public sealed record Transfer(string Id, string From, string To, decimal Amount);
