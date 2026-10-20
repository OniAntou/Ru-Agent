using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace RuAgent.Cli.Images;

/// <summary>
/// Reads an image from the Windows clipboard through raw Win32 handles (no WinForms):
/// copied files first, then a DIB payload written to a temp PNG, then a text value that
/// names an existing image. The returned temp path is what the attachment pipeline loads;
/// the caller deletes it once the attachment is sent or detached.
/// </summary>
public static class CliClipboard
{
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfDrop = 15;
    private const uint CfDibV5 = 17;
    private const long MaxDibBytes = 64 * 1024 * 1024;

    public sealed record ClipboardImage(string? Path, string? Error);

    /// <summary>
    /// Runs the clipboard read on a dedicated STA thread with a bounded join, so a wedged
    /// clipboard owner cannot hang the composer.
    /// </summary>
    public static ClipboardImage TryReadImagePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(null, "clipboard_paste_unavailable: only Windows terminals expose an image clipboard");
        }

        ClipboardImage? result = null;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    result = ReadSta();
                }
                catch (Exception ex)
                {
                    result = new(null, $"clipboard_paste_failed: {CliErrorText.ShortMessage(ex.Message)}");
                }
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(5)))
            {
                return new(null, "clipboard_busy: the clipboard did not answer in time");
            }
        }
        catch (Exception ex)
        {
            return new(null, $"clipboard_paste_failed: {CliErrorText.ShortMessage(ex.Message)}");
        }

        return result ?? new(null, "clipboard_paste_failed: the clipboard read returned nothing");
    }

    /// <summary>
    /// Reads the clipboard's plain text, for a composer that wants a text paste fallback
    /// when the clipboard holds no image. Bounded and retried like the image read.
    /// </summary>
    public static string? TryReadText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        string? result = null;
        try
        {
            var thread = new Thread(() =>
            {
                try
                {
                    if (!OpenClipboardWithRetry())
                    {
                        return;
                    }

                    try
                    {
                        result = ReadUnicodeText();
                    }
                    finally
                    {
                        NativeClipboard.CloseClipboard();
                    }
                }
                catch (Exception)
                {
                    // A text fallback that fails is simply no text.
                }
            })
            {
                IsBackground = true,
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            return null;
        }

        return result;
    }

    private static bool OpenClipboardWithRetry()
    {
        var opened = false;
        for (var attempt = 0; attempt < 5 && !opened; attempt++)
        {
            if (attempt > 0)
            {
                Thread.Sleep(80);
            }

            opened = NativeClipboard.OpenClipboard(IntPtr.Zero);
        }

        return opened;
    }

    private static ClipboardImage ReadSta()
    {
        var opened = OpenClipboardWithRetry();

        if (!opened)
        {
            return new(null, "clipboard_busy: another app is holding the clipboard");
        }

        try
        {
            var files = ReadFileDropPaths();
            foreach (var file in files)
            {
                if (CliImagePolicy.IsImageFile(file) && File.Exists(file))
                {
                    return new(file, null);
                }
            }

            if (files.Count > 0)
            {
                return new(null, "clipboard_files_not_images: the copied files are not images");
            }

            foreach (var format in new[] { CfDibV5, CfDib })
            {
                if (!NativeClipboard.IsClipboardFormatAvailable(format))
                {
                    continue;
                }

                var path = SaveDibFormatAsTempPng(format);
                return path is not null
                    ? new(path, null)
                    : new(null, "invalid_clipboard_image: the clipboard image could not be decoded");
            }

            var text = ReadUnicodeText();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var candidate = text.Trim().Trim('"', '\'');
                if (candidate.Length > 0
                    && candidate.Length <= 1024
                    && CliImagePolicy.IsImageFile(candidate)
                    && File.Exists(candidate))
                {
                    return new(candidate, null);
                }
            }
        }
        finally
        {
            NativeClipboard.CloseClipboard();
        }

        return new(null, "clipboard_has_no_image: copy a screenshot or an image file first");
    }

    private static List<string> ReadFileDropPaths()
    {
        var paths = new List<string>();
        if (!NativeClipboard.IsClipboardFormatAvailable(CfDrop))
        {
            return paths;
        }

        var handle = NativeClipboard.GetClipboardData(CfDrop);
        if (handle == IntPtr.Zero)
        {
            return paths;
        }

        var count = NativeClipboard.DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count && i < 16; i++)
        {
            var length = NativeClipboard.DragQueryFileW(handle, i, null, 0);
            var buffer = new StringBuilder((int)length + 1);
            NativeClipboard.DragQueryFileW(handle, i, buffer, (uint)buffer.Capacity);
            if (buffer.Length > 0)
            {
                paths.Add(buffer.ToString());
            }
        }

        return paths;
    }

    private static string? ReadUnicodeText()
    {
        var handle = NativeClipboard.GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var pointer = NativeClipboard.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            NativeClipboard.GlobalUnlock(handle);
        }
    }

    private static string? SaveDibFormatAsTempPng(uint format)
    {
        var handle = NativeClipboard.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (long)NativeClipboard.GlobalSize(handle);
        if (size is <= 0 or > MaxDibBytes)
        {
            return null;
        }

        var pointer = NativeClipboard.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        byte[] dib;
        try
        {
            dib = new byte[size];
            Marshal.Copy(pointer, dib, 0, (int)size);
        }
        finally
        {
            NativeClipboard.GlobalUnlock(handle);
        }

        return SaveDibAsTempPng(dib);
    }

    /// <summary>
    /// Writes a clipboard DIB to a temp PNG so the normal attachment pipeline can load it.
    /// Returns null when the payload is not decodable; the temp file never outlives a
    /// failed decode.
    /// </summary>
    public static string? SaveDibAsTempPng(byte[] dib)
    {
        string? path = null;
        try
        {
            var bmp = DibToBmp(dib);
            if (bmp is null)
            {
                return null;
            }

            using var stream = new MemoryStream(bmp, writable: false);
            using var image = Image.FromStream(stream);
            path = Path.Combine(Path.GetTempPath(), $"ru-img-{Guid.NewGuid():N}.png");
            image.Save(path, ImageFormat.Png);
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException or IOException)
        {
            if (path is not null)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception deleteEx) when (deleteEx is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup of a half-written temp file.
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Wraps a DIB payload in a BMP file header so GDI+ can decode it. Only the
    /// BITMAPINFOHEADER family (header ≥ 40 bytes) is accepted: the 12-byte core header
    /// puts its bit depth at a different offset, and guessing would mis-address pixels.
    /// </summary>
    public static byte[]? DibToBmp(byte[] dib)
    {
        if (dib.Length < 40)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            return null;
        }

        var bitCount = BitConverter.ToInt16(dib, 14);
        var colorsUsed = headerSize >= 36 ? BitConverter.ToInt32(dib, 32) : 0;
        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        var pixelOffset = 14 + headerSize + (paletteEntries * 4);

        using var stream = new MemoryStream(14 + dib.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write((uint)(14 + dib.Length));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((uint)pixelOffset);
        writer.Write(dib);
        return stream.ToArray();
    }

    private static class NativeClipboard
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalUnlock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        public static extern UIntPtr GlobalSize(IntPtr hMem);
    }
}
