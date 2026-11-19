using System.Text;

namespace RuAgent.Broker.Tools.Files;

/// <summary>
/// The next step a failed <c>read</c> gives the model (ADR 0053). A refusal that only says
/// "binary" or "invalid utf8" leaves the model guessing; naming what the file looks like
/// and the command that opens it lets the model carry on by itself, the way the other coding
/// agents' read tools do. The hint never carries file content beyond a short magic-byte
/// signature, and it names the file only as &lt;path&gt;.
/// </summary>
internal static class FileReadFailureHints
{
    /// <summary>Bytes of the file head a hint inspects.</summary>
    public const int SniffBytes = 64;

    /// <summary>The key a repair hint travels under in <see cref="Exception.Data"/>.</summary>
    public const string ExceptionDataKey = "ru.repair_hint";

    /// <summary>Attaches a repair hint without changing the exception's type or code.</summary>
    public static TException WithHint<TException>(TException error, string hint)
        where TException : Exception
    {
        error.Data[ExceptionDataKey] = hint;
        return error;
    }

    /// <summary>The repair hint an adapter attached, if any.</summary>
    public static string? Of(Exception error) => error.Data[ExceptionDataKey] as string;

    /// <summary>A file that is not UTF-8 text but holds no NUL byte: a text file in another encoding.</summary>
    public static string NotUtf8(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return "The file is UTF-16 text, and read decodes UTF-8 only. Read it with a shell command, "
                + "for example `Get-Content -Encoding Unicode <path>`, or convert a copy to UTF-8 and read that.";
        }

        return "The file is not valid UTF-8; it is probably text in a legacy code page (for example Windows-1252 or Shift-JIS). "
            + "Read it with a shell command, for example `Get-Content -Encoding Default <path>` in Windows PowerShell, "
            + "or convert a copy to UTF-8 and read that. Do not retry read unchanged.";
    }

    /// <summary>A binary file: what it appears to be, and the command that inspects it.</summary>
    public static string Binary(ReadOnlySpan<byte> head, long totalBytes)
    {
        var size = FormatBytes(totalBytes);
        // UTF-16 text is full of NUL bytes, so it reaches this branch; it is still text.
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return NotUtf8(head);
        if (head.StartsWith("PK\u0003\u0004"u8))
            // PowerShell's own cmdlet, not `tar`: the tar first on PATH (Git's GNU tar) cannot
            // read ZIP, which a live run hit.
            return $"This is a ZIP archive ({size}); read does not open archives. List it with "
                + "`[IO.Compression.ZipFile]::OpenRead('<path>').Entries.FullName` (Add-Type -AssemblyName System.IO.Compression.FileSystem first in Windows PowerShell), "
                + "extract it into the workspace with `Expand-Archive <path> -DestinationPath <folder>`, then read the file you need.";
        if (head.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
            return $"This is a gzip archive ({size}). List it with `tar -tzf <path>`, extract what you need into the workspace, then read that file.";
        if (head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]) || head.StartsWith("Rar!"u8))
            return $"This is a 7z or RAR archive ({size}). List it with `7z l <path>` if 7-Zip is installed, or ask the user to extract it.";
        if (head.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
            return $"This is a legacy Office binary file or a password-protected document ({size}). Ask the user to save it as .docx, .xlsx, .pptx or PDF, "
                + "or convert it with `soffice --headless --convert-to docx <path>` if LibreOffice is installed.";
        if (head.StartsWith("MZ"u8))
            return $"This is a Windows executable or library ({size}); it has no text to read. Inspect its version information with "
                + "`(Get-Item <path>).VersionInfo`, or its strings with a shell command, if the task needs it.";
        if (head.StartsWith((ReadOnlySpan<byte>)[0x7F, 0x45, 0x4C, 0x46]))
            return $"This is an ELF executable ({size}); it has no text to read.";
        if (head.StartsWith("SQLite format 3\0"u8))
            return $"This is a SQLite database ({size}). Query it with a shell command, for example `sqlite3 <path> .tables` if sqlite3 is installed, "
                + "or a short Python script using the sqlite3 module.";
        if (head.StartsWith("%PDF-"u8))
            return $"This is a PDF without a .pdf extension ({size}). Copy it to a name ending in .pdf inside the workspace and read that.";

        return $"This file is binary ({size}; first bytes {Signature(head)}), so read has no text to return. "
            + "Inspect it with a shell command, for example `Format-Hex <path> -Count 256`, or use a tool or skill made for its format.";
    }

    /// <summary>A legacy or Apple document format read cannot parse.</summary>
    public static string UnsupportedDocument(string extension) =>
        $"read extracts .docx, .xlsx, .pptx, OpenDocument and PDF, but not {extension} files. Ask the user to save it as .docx, .xlsx, .pptx or PDF, "
        + "or convert it with a shell command, for example `soffice --headless --convert-to pdf <path>` if LibreOffice is installed.";

    /// <summary>A path outside the workspace, which file tools cannot open in plan or standard.</summary>
    public const string OutsideWorkspace =
        "The path is outside the workspace, so the file tools cannot open it. Ask the user to attach the file in the chat "
        + "(the composer copies an outside file into the workspace's .ru/attachments folder) or to open its folder as the workspace.";

    private static string Signature(ReadOnlySpan<byte> head)
    {
        var builder = new StringBuilder();
        foreach (var value in head[..Math.Min(head.Length, 8)])
        {
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };
}
