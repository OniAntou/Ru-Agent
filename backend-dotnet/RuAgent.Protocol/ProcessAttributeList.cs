using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Protocol;

/// <summary>
/// One <c>PROC_THREAD_ATTRIBUTE_LIST</c> for a single child process creation.
/// It carries <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>, restricting inheritance.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessAttributeList : IDisposable
{
    private const int ErrorInsufficientBuffer = 122;
    private const int HandleListAttribute = 0x00020002;
    public IntPtr Pointer { get; private set; }

    public IntPtr Values { get; private set; }

    public static ProcessAttributeList Create(
        IReadOnlyList<SafeFileHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var size = IntPtr.Zero;
        if (!InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size)
            && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the child attribute list.");
        }

        var owner = new ProcessAttributeList();
        var isInitialized = false;
        try
        {
            owner.Pointer = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(owner.Pointer, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the child attribute list.");
            isInitialized = true;
            owner.AddHandleList(handles);
            return owner;
        }
        catch
        {
            owner.Release(isInitialized);
            throw;
        }
    }

    public void Dispose() => Release(isInitialized: true);

    private void AddHandleList(IReadOnlyList<SafeFileHandle> handles)
    {
        var bytes = checked(handles.Count * IntPtr.Size);
        Values = Marshal.AllocHGlobal(bytes);
        for (var index = 0; index < handles.Count; index++)
        {
            Marshal.WriteIntPtr(Values, index * IntPtr.Size, handles[index].DangerousGetHandle());
        }

        if (!UpdateProcThreadAttribute(Pointer, 0, (IntPtr)HandleListAttribute,
                Values, (IntPtr)bytes, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict child handle inheritance.");
        }
    }

    private void Release(bool isInitialized)
    {
        if (Pointer != IntPtr.Zero)
        {
            if (isInitialized) DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Pointer = IntPtr.Zero;
        }
        if (Values != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(Values);
            Values = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
}
