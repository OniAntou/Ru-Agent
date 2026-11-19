using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools.Files;

public sealed partial class FileReadAdapter
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;
    private readonly Func<string, FileStream> _lineReadOpener;

    public FileReadAdapter(string workspaceRoot, bool allowProtectedPaths = false)
        : this(workspaceRoot, OpenLineRead, allowProtectedPaths)
    {
    }

    internal FileReadAdapter(string workspaceRoot, Func<string, FileStream> lineReadOpener, bool allowProtectedPaths = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(lineReadOpener);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot, allowProtectedPaths);
        _lineReadOpener = lineReadOpener;
    }

    /// <summary>
    /// Blocking test convenience. Production dispatch awaits <see cref="ExecuteAsync"/>
    /// with the run's cancellation token; this wrapper can do neither.
    /// </summary>
    internal JsonObject Execute(JsonObject arguments) =>
        ExecuteAsync(arguments, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <summary>Text-only read: an image file fails with <c>image_unsupported_by_model</c>.</summary>
    public async ValueTask<JsonObject> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken) =>
        (await ExecuteAsync(arguments, imageInputAccepted: false, cancellationToken).ConfigureAwait(false)).Output;

    /// <summary>
    /// Reads a text file, or an image file when <paramref name="imageInputAccepted"/> says
    /// the run's model accepts image input (ADR 0026).
    /// </summary>
    public async ValueTask<FileReadResult> ExecuteAsync(
        JsonObject arguments,
        bool imageInputAccepted,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetRequiredString(arguments, "path");
        if (path.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        var offset = 0;
        if (arguments.TryGetPropertyValue("offset", out var offNode) && offNode is not null)
        {
            if (offNode is JsonValue ov && ov.TryGetValue<int>(out var ovInt))
            {
                if (ovInt < 0)
                {
                    throw new ArgumentException("invalid_offset");
                }

                offset = ovInt;
            }
            else if (offNode is JsonValue ovLong && ovLong.TryGetValue<long>(out var ovLongVal))
            {
                if (ovLongVal < 0 || ovLongVal > int.MaxValue)
                {
                    throw new ArgumentException("invalid_offset");
                }

                offset = (int)ovLongVal;
            }
            else
            {
                throw new ArgumentException("invalid_offset");
            }
        }

        var limit = FileToolLimits.DefaultReadLimit;
        if (arguments.TryGetPropertyValue("limit", out var limNode) && limNode is not null)
        {
            if (limNode is JsonValue lv && lv.TryGetValue<int>(out var lvInt))
            {
                if (lvInt < 1 || lvInt > FileToolLimits.MaxReadBytes)
                {
                    throw new ArgumentException("invalid_limit");
                }

                limit = lvInt;
            }
            else if (limNode is JsonValue lvLong && lvLong.TryGetValue<long>(out var lvLongVal))
            {
                if (lvLongVal < 1 || lvLongVal > FileToolLimits.MaxReadBytes)
                {
                    throw new ArgumentException("invalid_limit");
                }

                limit = (int)lvLongVal;
            }
            else
            {
                throw new ArgumentException("invalid_limit");
            }
        }

        string? pages = null;
        if (arguments.TryGetPropertyValue("pages", out var pagesNode) && pagesNode is not null)
        {
            if (pagesNode is not JsonValue pagesValue
                || !pagesValue.TryGetValue<string>(out var pagesText)
                || string.IsNullOrWhiteSpace(pagesText)
                || pagesText.Length > 32)
            {
                throw new ArgumentException("invalid_pages");
            }

            pages = pagesText.Trim();
        }

        var resolved = ResolveChecked(path);
        var canonical = resolved.CanonicalPath;

        if (!File.Exists(canonical))
        {
            if (Directory.Exists(canonical))
            {
                throw new IOException("is_directory");
            }

            throw new FileNotFoundException("file_not_found");
        }

        if (WorkspaceImageProbe.HasImageExtension(canonical))
        {
            return await ReadImageAsync(canonical, offset, imageInputAccepted, cancellationToken).ConfigureAwait(false);
        }

        if (string.Equals(Path.GetExtension(canonical), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadPdfAsync(canonical, offset, pages, cancellationToken).ConfigureAwait(false);
        }

        if (FileOfficeTextExtractor.IsSupported(Path.GetExtension(canonical)))
        {
            return await ReadOfficeAsync(canonical, offset, pages, cancellationToken).ConfigureAwait(false);
        }

        if (IsUnsupportedDocumentExtension(canonical))
        {
            // Legacy binary Office and Apple formats have no in-process parser (ADR 0053);
            // the hint names the conversion that makes them readable.
            throw new FileReadDocumentException(
                "document_unsupported",
                FileReadFailureHints.UnsupportedDocument(Path.GetExtension(canonical).ToLowerInvariant()));
        }

        var sw = Stopwatch.StartNew();
        var buffer = new byte[limit];
        long observedFileLength;
        int bytesRead = 0;
        string sha256;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(FileToolLimits.MaxTimeMs);
            try
            {
                using var stream = HardLinkGuard.OpenRead(
                    canonical,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, canonical);
                observedFileLength = stream.Length;
                stream.Seek(offset, SeekOrigin.Begin);
                while (bytesRead < limit)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    int n = await stream.ReadAsync(buffer.AsMemory(bytesRead, limit - bytesRead), budget.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    bytesRead += n;
                }

                stream.Seek(0, SeekOrigin.Begin);
                sha256 = await FileHashService.ComputeStreamHashAsync(stream, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("read_hash_timeout");
            }
        }

        // Keep the arithmetic in Int64: a valid Int32 offset near EOF can
        // otherwise wrap when the requested window crosses 2 GiB, incorrectly
        // reporting a complete read.
        bool isMore = ((long)offset + bytesRead) < observedFileLength;
        int validLength = bytesRead;
        bool utfTruncated = false;
        if (bytesRead > 0)
        {
            if (offset > 0 && (buffer[0] & 0xC0) == 0x80)
                throw new ArgumentException("invalid_utf8_offset");
            try
            {
                validLength = GetValidUtf8Length(buffer, bytesRead, allowIncompleteTail: isMore);
            }
            catch (ArgumentException error) when (error.Message == "invalid_utf8")
            {
                throw LooksBinary(buffer, bytesRead)
                    ? BinaryRefusal(buffer.AsSpan(0, bytesRead), observedFileLength, error)
                    : FileReadFailureHints.WithHint(error, FileReadFailureHints.NotUtf8(buffer.AsSpan(0, bytesRead)));
            }

            if (validLength == 0)
                throw new ArgumentException("read_limit_too_small");
            if (validLength < bytesRead)
            {
                utfTruncated = true;
            }
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(buffer, 0, validLength);
        }
        catch (DecoderFallbackException ex)
        {
            if (LooksBinary(buffer, validLength)) throw BinaryRefusal(buffer.AsSpan(0, validLength), observedFileLength, ex);
            throw FileReadFailureHints.WithHint(
                new ArgumentException("invalid_utf8", ex),
                FileReadFailureHints.NotUtf8(buffer.AsSpan(0, validLength)));
        }

        bool truncated = isMore || utfTruncated || validLength < bytesRead;

        var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, canonical);
        var output = new JsonObject
        {
            ["path"] = relative,
            ["content"] = content,
            ["truncated"] = truncated,
            ["bytes_read"] = validLength,
            ["total_bytes"] = observedFileLength,
            ["offset"] = offset,
            ["duration_ms"] = (int)sw.ElapsedMilliseconds,
            ["sha256"] = sha256,
        };
        var fitted = FileReadOutputBudget.Fit(output);
        var fittedContent = fitted["content"]!.GetValue<string>();
        // A model reads an empty result as a broken tool. Say what actually happened,
        // and when content remains, say where to continue.
        if (fitted["truncated"]!.GetValue<bool>())
        {
            fitted["notice"] = fittedContent.Length < content.Length
                ? "content was truncated at the output budget; continue at next_offset."
                : $"more content remains ({observedFileLength} bytes total); continue at next_offset.";
        }
        else if (validLength == 0 && observedFileLength == 0)
        {
            fitted["notice"] = "the file exists but is empty (0 bytes).";
        }
        else if (validLength == 0)
        {
            fitted["notice"] =
                $"offset {offset} is at or past the end of the file ({observedFileLength} bytes); nothing to read there.";
        }

        // Token estimate, not a byte count: the model budgets in tokens, and a byte
        // number it cannot convert invites blind paging.
        fitted["estimated_tokens"] = (fittedContent.Length + 3) / 4;
        return new FileReadResult(fitted, null);
    }

    private async ValueTask<FileReadResult> ReadImageAsync(
        string canonical,
        int offset,
        bool imageInputAccepted,
        CancellationToken cancellationToken)
    {
        if (offset != 0)
        {
            throw new FileReadImageException(
                "image_offset_unsupported",
                "An image is read whole. Retry without offset.");
        }

        var sw = Stopwatch.StartNew();
        byte[] bytes;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(FileToolLimits.MaxTimeMs);
            try
            {
                using var stream = HardLinkGuard.OpenRead(
                    canonical,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, canonical);
                if (stream.Length > ToolOutputImage.MaximumBytes)
                {
                    throw TooLarge();
                }

                // One byte past the bound detects a file that grew after the length check.
                var buffer = new byte[ToolOutputImage.MaximumBytes + 1];
                var length = 0;
                while (length < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(length), budget.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    length += n;
                }

                if (length > ToolOutputImage.MaximumBytes)
                {
                    throw TooLarge();
                }

                bytes = buffer[..length];
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("read_hash_timeout");
            }
        }

        var mediaType = WorkspaceImageProbe.SniffMediaType(bytes) ?? throw InvalidFormat();
        if (!imageInputAccepted)
        {
            throw new FileReadImageException(
                "image_unsupported_by_model",
                "This run's model does not accept image input, so the image cannot be shown to it. Tell the user, or ask them to choose a vision model.");
        }

        var size = WorkspaceImageProbe.ReadDimensions(mediaType, bytes) ?? throw InvalidFormat();
        if (!WorkspaceImageProbe.IsWithinDimensionBound(size))
        {
            throw new FileReadImageException(
                "image_dimensions_too_large",
                $"The image is larger than {ToolOutputImage.MaximumDimension} px on a side. Ask the user for a smaller copy.");
        }

        var output = new JsonObject
        {
            ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, canonical),
            ["media_type"] = mediaType,
            ["total_bytes"] = bytes.Length,
            ["width"] = size.Width,
            ["height"] = size.Height,
            ["duration_ms"] = (int)sw.ElapsedMilliseconds,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        };
        return new FileReadResult(output, new ToolOutputImage(mediaType, bytes));

        static FileReadImageException TooLarge() => new(
            "image_too_large",
            $"The image is larger than {ToolOutputImage.MaximumBytes / (1024 * 1024)} MiB. Ask the user for a smaller copy.");

        static FileReadImageException InvalidFormat() => new(
            "image_format_invalid",
            "The file has an image extension but is not a readable PNG, JPEG or WebP image.");
    }

    private async ValueTask<FileReadResult> ReadPdfAsync(
        string canonical,
        int offset,
        string? pages,
        CancellationToken cancellationToken)
    {
        if (offset != 0)
        {
            throw new FileReadDocumentException(
                "pdf_offset_unsupported",
                "PDFs are read by page range; call read with pages=\"1-10\" instead of offset.");
        }

        var sw = Stopwatch.StartNew();
        byte[] bytes;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(FileToolLimits.MaxDocumentTimeMs);
            try
            {
                using var stream = HardLinkGuard.OpenRead(
                    canonical,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, canonical);
                if (stream.Length > FileToolLimits.MaxDocumentBytes)
                {
                    throw PdfTooLarge();
                }

                // One byte past the bound detects a file that grew after the length check.
                var buffer = new byte[(int)Math.Min(stream.Length, FileToolLimits.MaxDocumentBytes) + 1];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), budget.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }

                if (length > FileToolLimits.MaxDocumentBytes)
                {
                    throw PdfTooLarge();
                }

                bytes = buffer[..length];
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FileReadDocumentException(
                    "pdf_read_timeout",
                    "Reading the PDF took too long. Ask the user for a smaller copy or a text export.");
            }
        }

        var extracted = FilePdfTextExtractor.Extract(bytes, pages, cancellationToken);
        var output = new JsonObject
        {
            ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, canonical),
            ["kind"] = "pdf",
            ["format"] = "pdf",
            ["page_count"] = extracted.PageCount,
            ["pages"] = extracted.FirstPage == extracted.LastPage
                ? extracted.FirstPage.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{extracted.FirstPage}-{extracted.LastPage}",
            ["content"] = extracted.Text,
            ["chars_read"] = extracted.Text.Length,
            ["truncated"] = extracted.MorePages || extracted.TruncatedByCharacters,
            ["total_bytes"] = bytes.Length,
            ["duration_ms"] = (int)sw.ElapsedMilliseconds,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["estimated_tokens"] = (extracted.Text.Length + 3) / 4,
        };
        if (extracted.Title is { } title)
        {
            output["title"] = title;
        }

        var notices = new List<string>();
        if (extracted.Text.Length == 0)
        {
            notices.Add("No extractable text: this PDF may be a scan or image-only. Convert or OCR it first, or ask the user for a text copy.");
        }

        if (extracted.MorePages)
        {
            var nextFirst = extracted.LastPage + 1;
            var nextLast = Math.Min(extracted.LastPage + FilePdfTextExtractor.MaxPagesPerRead, extracted.PageCount);
            notices.Add($"More pages remain ({extracted.PageCount} total); continue with pages=\"{nextFirst}-{nextLast}\".");
        }

        if (extracted.TruncatedByCharacters)
        {
            notices.Add("Page text was truncated at the extraction character cap; read a smaller page range.");
        }

        if (notices.Count > 0)
        {
            output["notice"] = string.Join(" ", notices);
        }

        return new FileReadResult(output, null);

        static FileReadDocumentException PdfTooLarge() => new(
            "pdf_too_large",
            $"The PDF is larger than {FileToolLimits.MaxDocumentBytes / (1024 * 1024)} MiB. Ask the user for a smaller copy or a text export.");
    }

    /// <summary>
    /// Office Open XML and OpenDocument text (ADR 0053), paged by part (paragraph, slide or
    /// row) through <c>pages</c>, the same argument a PDF uses for its page range.
    /// </summary>
    private async ValueTask<FileReadResult> ReadOfficeAsync(
        string canonical,
        int offset,
        string? pages,
        CancellationToken cancellationToken)
    {
        if (offset != 0)
        {
            throw new FileReadDocumentException(
                "document_offset_unsupported",
                "Documents are read by part range; call read with pages=\"1-200\" (paragraphs, slides or rows) instead of offset.");
        }

        var sw = Stopwatch.StartNew();
        FileOfficeText extracted;
        byte[] bytes;
        using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            budget.CancelAfter(FileToolLimits.MaxDocumentTimeMs);
            try
            {
                using var stream = HardLinkGuard.OpenRead(
                    canonical,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, canonical);
                if (stream.Length > FileToolLimits.MaxDocumentBytes)
                {
                    throw DocumentTooLarge();
                }

                // One byte past the bound detects a file that grew after the length check.
                var buffer = new byte[(int)Math.Min(stream.Length, FileToolLimits.MaxDocumentBytes) + 1];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(length), budget.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }

                if (length > FileToolLimits.MaxDocumentBytes)
                {
                    throw DocumentTooLarge();
                }

                bytes = buffer[..length];
                extracted = FileOfficeTextExtractor.Extract(bytes, Path.GetExtension(canonical), pages, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new FileReadDocumentException(
                    "document_read_timeout",
                    "Reading the document took too long. Read a smaller pages range, or ask the user for a PDF or text export.");
            }
        }

        var output = new JsonObject
        {
            ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, canonical),
            ["kind"] = "document",
            ["format"] = extracted.Format,
            ["unit"] = extracted.Unit,
            ["part_count"] = extracted.PartCount,
            ["pages"] = extracted.PartCount == 0
                ? "0"
                : extracted.FirstPart == extracted.LastPart
                    ? extracted.FirstPart.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : $"{extracted.FirstPart}-{extracted.LastPart}",
            ["content"] = extracted.Text,
            ["chars_read"] = extracted.Text.Length,
            ["truncated"] = extracted.MoreParts || extracted.TruncatedByCharacters,
            ["total_bytes"] = bytes.Length,
            ["duration_ms"] = (int)sw.ElapsedMilliseconds,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ["estimated_tokens"] = (extracted.Text.Length + 3) / 4,
        };

        if (extracted.PartCount == 0 || extracted.Text.Trim().Length == 0)
        {
            output["notice"] = "No extractable text: the document may hold only images or embedded objects. Ask the user for a PDF or text export.";
        }
        else if (extracted.TruncatedByCharacters)
        {
            // Re-reading the part the cap cut keeps it whole; a single part longer than the
            // cap moves on to the next one instead of looping.
            var next = extracted.LastPart > extracted.FirstPart ? extracted.LastPart : extracted.LastPart + 1;
            output["notice"] = next > extracted.PartCount
                ? $"Text was cut at {FileOfficeTextExtractor.MaxCharacters} characters inside {extracted.Unit} {extracted.LastPart}, the last one."
                : $"Text was cut at {FileOfficeTextExtractor.MaxCharacters} characters ({extracted.PartCount} {extracted.Unit}s in total); continue with pages=\"{next}-{extracted.PartCount}\".";
        }
        else if (extracted.MoreParts)
        {
            output["notice"] = $"More remains ({extracted.PartCount} {extracted.Unit}s in total); continue with pages=\"{extracted.LastPart + 1}-{extracted.PartCount}\".";
        }

        return new FileReadResult(output, null);

        static FileReadDocumentException DocumentTooLarge() => new(
            "document_too_large",
            $"The document is larger than {FileToolLimits.MaxDocumentBytes / (1024 * 1024)} MiB. Ask the user for a smaller copy, a PDF or a text export.");
    }

    private static bool LooksBinary(byte[] buffer, int length) =>
        buffer.AsSpan(0, length).Contains((byte)0);

    private static FileReadDocumentException BinaryRefusal(ReadOnlySpan<byte> head, long totalBytes, Exception? inner = null) => new(
        "binary_file_unsupported",
        FileReadFailureHints.Binary(head[..Math.Min(head.Length, FileReadFailureHints.SniffBytes)], totalBytes),
        inner);

    /// <summary>
    /// Document formats with no in-process parser: legacy binary Office and Apple iWork.
    /// RTF is not listed: it is 7-bit text and reads as such.
    /// </summary>
    private static bool IsUnsupportedDocumentExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".doc" or ".dot" or ".xls" or ".xlt" or ".ppt" or ".pot" or ".pps"
            or ".pages" or ".numbers" or ".key";

    private ResolvedPath ResolveChecked(string input)
    {
        try
        {
            var resolved = _resolver.Resolve(input);
            if (resolved.IsProtected)
            {
                throw new UnauthorizedAccessException("protected_path");
            }

            if (resolved.IsOutsideWorkspace)
            {
                throw FileReadFailureHints.WithHint(
                    new UnauthorizedAccessException("outside_workspace"),
                    FileReadFailureHints.OutsideWorkspace);
            }

            return resolved;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("invalid_path", ex);
        }
    }

    private static int GetValidUtf8Length(byte[] buffer, int length, bool allowIncompleteTail)
    {
        var bytes = buffer.AsSpan(0, length);
        var consumed = 0;
        while (consumed < length)
        {
            var status = Rune.DecodeFromUtf8(bytes[consumed..], out _, out var advance);
            if (status == OperationStatus.NeedMoreData && allowIncompleteTail)
                return consumed;
            if (status != OperationStatus.Done)
                throw new ArgumentException("invalid_utf8");
            consumed += advance;
        }
        return length;
    }

    private static string GetRequiredString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }
}

/// <summary>A read's JSON output and, for an image file, the image beside it.</summary>
public sealed record FileReadResult(JsonObject Output, ToolOutputImage? Image);

/// <summary>A named read refusal with a repair hint for the model.</summary>
public abstract class FileReadRefusalException(string code, string hint, Exception? inner = null)
    : IOException(code, inner)
{
    public string Code { get; } = code;

    public string Hint { get; } = hint;
}

/// <summary>A named image-read failure with a repair hint for the model (ADR 0026).</summary>
public sealed class FileReadImageException(string code, string hint)
    : FileReadRefusalException(code, hint);

/// <summary>A named document-read failure with a repair hint for the model (PDF, Office, binary).</summary>
public sealed class FileReadDocumentException(string code, string hint, Exception? inner = null)
    : FileReadRefusalException(code, hint, inner);
