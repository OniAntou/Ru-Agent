using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileMultiEditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-multiedit-" + Guid.NewGuid().ToString("N"));

    public FileMultiEditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Replacements_use_original_text_and_preserve_crlf()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\r\nbeta\r\n");
        var result = Apply(("alpha", "beta"), ("beta", "gamma"));
        Assert.Equal("beta\r\ngamma\r\n", File.ReadAllText(Path.Combine(_root, "a.txt")));
        Assert.Equal(2, result["replacements"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("alpha beta", "alpha", "missing", "edit_no_match")]
    [InlineData("alpha alpha beta", "alpha", "beta", "edit_not_unique")]
    [InlineData("alpha beta", "alpha", "alpha beta", "edit_overlap")]
    public void Invalid_later_edit_leaves_original_untouched(string original, string first, string second, string code)
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), original);
        var error = Assert.ThrowsAny<Exception>(() => Apply((first, "changed"), (second, "changed")));
        Assert.Equal(code, error.Message);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    [Fact]
    public void A_stale_preimage_never_overwrites_current_file()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "newer");
        var args = Arguments(("newer", "changed"));
        args["expected_sha256"] = FileHashService.ComputeStringHash("older");
        Assert.Throws<UnauthorizedAccessException>(() => new FileEditAdapter(_root).ExecuteMany(args));
        Assert.Equal("newer", File.ReadAllText(Path.Combine(_root, "a.txt")));
    }

    private JsonObject Apply(params (string Old, string New)[] edits) => new FileEditAdapter(_root).ExecuteMany(Arguments(edits));

    private JsonObject Arguments(params (string Old, string New)[] edits) => new()
    {
        ["path"] = "a.txt",
        ["expected_sha256"] = FileHashService.GetCurrentHash(Path.Combine(_root, "a.txt")),
        ["edits"] = new JsonArray(edits.Select(edit => (JsonNode)new JsonObject
        {
            ["old_string"] = edit.Old, ["new_string"] = edit.New,
        }).ToArray()),
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
