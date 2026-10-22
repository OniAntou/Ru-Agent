using System.Runtime.InteropServices;

namespace RuApp.Services;

[ComVisible(true), Guid("fec87aaf-35f9-447a-adb7-20234491401a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewHandlerFrame
{
    [PreserveSig] int GetWindowContext(out PreviewFrameInfo info);
    [PreserveSig] int TranslateAccelerator(ref PreviewMessage message);
}

[ComVisible(true), Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPreviewSiteWindow
{
    void GetWindow(out nint hwnd);
    [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
}

[StructLayout(LayoutKind.Sequential)] public struct PreviewFrameInfo { public nint Accelerator; public uint Entries; }
[StructLayout(LayoutKind.Sequential)] public struct PreviewMessage { public nint Hwnd; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }

/// <summary>Office previewers query both frame and IOleWindow through their site.</summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class PreviewHandlerSite(nint parent, Action leavePreview) : IPreviewHandlerFrame, IPreviewSiteWindow
{
    public void GetWindow(out nint hwnd) => hwnd = parent;
    public int ContextSensitiveHelp(bool enter) => unchecked((int)0x80004001);
    public int GetWindowContext(out PreviewFrameInfo info)
    {
        // Handlers may use this table to decide which keys to forward. An empty
        // successful table prevents Tab/Escape from reaching TranslateAccelerator.
        var keys = new[]
        {
            new Accelerator { Flags = 1, Key = 9, Command = 1 },
            new Accelerator { Flags = 5, Key = 9, Command = 1 },
            new Accelerator { Flags = 1, Key = 27, Command = 1 },
        };
        info = new() { Accelerator = CreateAcceleratorTable(keys, keys.Length), Entries = (uint)keys.Length };
        // The preview-handler contract transfers ownership of this table to the handler.
        return info.Accelerator != 0 ? 0 : unchecked((int)0x80004005);
    }
    public int TranslateAccelerator(ref PreviewMessage message)
    {
        if (message.Id == 0x100 && message.WParam is 9 or 27) { leavePreview(); return 0; }
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Accelerator { public byte Flags; public ushort Key, Command; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateAcceleratorTable(Accelerator[] entries, int count);
}
