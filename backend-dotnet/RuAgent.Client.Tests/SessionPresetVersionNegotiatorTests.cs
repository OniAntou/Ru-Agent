using Xunit;

namespace RuAgent.Client.Tests;

public sealed class SessionPresetVersionNegotiatorTests
{
    [Fact]
    public async Task RetriesUsingAuthoritativeCurrentVersionBeyondLegacyGuessRange()
    {
        var attempted = new List<long>();
        var version = await SessionPresetVersionNegotiator.ExecuteAsync("session-a", expected =>
        {
            attempted.Add(expected);
            if (expected == 0)
            {
                throw Conflict("session-a", 17);
            }

            return Task.FromResult(expected + 1);
        });

        Assert.Equal(new long[] { 0, 17 }, attempted);
        Assert.Equal(18, version);
    }

    [Fact]
    public async Task RejectsMissingOrMismatchedConflictOwnership()
    {
        foreach (var (sessionId, version) in new (string? SessionId, long? Version)[]
        {
            (null, 17),
            ("session-b", 17),
            ("session-a", null),
            ("session-a", -1),
        })
        {
            var calls = 0;
            var error = await Assert.ThrowsAsync<AgentApiException>(() =>
                SessionPresetVersionNegotiator.ExecuteAsync<long>("session-a", _ =>
                {
                    calls++;
                    throw new AgentApiException(
                        "preset_version_conflict",
                        "conflict",
                        409,
                        presetSessionId: sessionId,
                        currentPresetVersion: version);
                }));

            Assert.Equal("preset_version_conflict", error.ErrorCode);
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task RejectsRepeatedVersionAndStopsAtBoundedAttempts()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<AgentApiException>(() =>
            SessionPresetVersionNegotiator.ExecuteAsync<long>("session-a", expected =>
            {
                calls++;
                throw Conflict("session-a", expected == 0 ? 7 : 7);
            }));

        Assert.Equal("preset_version_conflict", error.ErrorCode);
        Assert.Equal(2, calls);
    }

    private static AgentApiException Conflict(string sessionId, long version) =>
        new("preset_version_conflict", "conflict", 409, presetSessionId: sessionId, currentPresetVersion: version);
}
