using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Protocol;

internal sealed class ProcessHandleList : IDisposable
{
    private const int ErrorInsufficientBuffer = 122;
    private const int HandleListAttribute = 0x00020002;

    public IntPtr Pointer { get; private set; }
    public IntPtr Values { get; private set; }

    public static ProcessHandleList Create(IReadOnlyList<SafeFileHandle> handles)
    {
        var size = IntPtr.Zero;
        if (!InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size)
            && Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the child handle list.");
        }

        var owner = new ProcessHandleList();
        var isInitialized = false;
        try
        {
            owner.Pointer = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(owner.Pointer, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the child handle list.");
            isInitialized = true;
            var bytes = checked(handles.Count * IntPtr.Size);
            owner.Values = Marshal.AllocHGlobal(bytes);
            for (var index = 0; index < handles.Count; index++)
                Marshal.WriteIntPtr(owner.Values, index * IntPtr.Size, handles[index].DangerousGetHandle());
            if (!UpdateProcThreadAttribute(owner.Pointer, 0, (IntPtr)HandleListAttribute,
                    owner.Values, (IntPtr)bytes, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict child handle inheritance.");
            return owner;
        }
        catch
        {
            owner.Release(isInitialized);
            throw;
        }
    }

    public void Dispose() => Release(isInitialized: true);

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
