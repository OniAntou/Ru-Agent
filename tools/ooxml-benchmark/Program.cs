using System.Diagnostics;
using OoxmlBenchmark;
using RuAgent.Client;

var fixtures = new (string FileName, byte[] Bytes, string[] Expected, (string Before, string After)[] Order)[]
{
    ("docx-easy.docx", Fixtures.BuildDocxEasy(),
        ["First line", "Second line"], [("First line", "Second line")]),
    ("docx-hard.docx", Fixtures.BuildDocxHard(),
        ["Bảng dữ liệu", "Ô 1", "Ô 2", "Ô 3", "Ô 4", "Tiếng Việt: đồ ăn, phở, xe máy"],
        [("Bảng dữ liệu", "Ô 1"), ("Ô 1", "Ô 3")]),
    ("xlsx-easy.xlsx", Fixtures.BuildXlsxEasy(),
        ["shared alpha", "shared beta"], [("shared alpha", "shared beta")]),
    ("xlsx-hard.xlsx", Fixtures.BuildXlsxHard(),
        ["inline one", "inline two", "42", "45123", "shared text", "Sheet: Đầu", "Sheet: Hai"],
        [("Sheet: Đầu", "Sheet: Hai"), ("inline one", "42")]),
    ("pptx-easy.pptx", Fixtures.BuildPptxEasy(),
        ["One", "Two", "Slide 1", "Slide 2"], [("One", "Two")]),
    ("pptx-hard.pptx", Fixtures.BuildPptxHard(),
        ["One", "Two", "T1", "T2", "Slide 1", "Slide 2"], [("One", "Two"), ("T1", "T2")]),
};

var failed = 0;
foreach (var (fileName, bytes, expected, order) in fixtures)
{
    var stopwatch = Stopwatch.StartNew();
    var extraction = OfficeDocumentText.Extract(fileName, bytes);
    stopwatch.Stop();

    var missing = expected.Where(marker => !extraction.Text.Contains(marker, StringComparison.Ordinal)).ToArray();
    var misordered = order.Where(pair => Index(extraction.Text, pair.Before) >= Index(extraction.Text, pair.After)).ToArray();
    if (extraction.Result != OfficeDocumentText.Result.Ok || missing.Length != 0 || misordered.Length != 0)
    {
        failed++;
        Console.WriteLine($"FAIL {fileName}: result={extraction.Result} chars={extraction.Text.Length} "
            + $"missing=[{string.Join(", ", missing)}] "
            + $"misordered=[{string.Join(", ", misordered.Select(pair => $"{pair.Before}->{pair.After}"))}]");
        continue;
    }

    Console.WriteLine($"PASS {fileName}: chars={extraction.Text.Length} markers={expected.Length} "
        + $"elapsed={stopwatch.ElapsedMilliseconds}ms");
}

Console.WriteLine(failed == 0
    ? $"All {fixtures.Length} fixtures extracted with every marker present."
    : $"{failed} of {fixtures.Length} fixtures failed.");
return failed == 0 ? 0 : 1;

static int Index(string text, string marker) => text.IndexOf(marker, StringComparison.Ordinal);
