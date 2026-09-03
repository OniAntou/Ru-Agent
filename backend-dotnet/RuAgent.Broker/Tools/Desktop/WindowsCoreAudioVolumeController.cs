using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

[assembly: InternalsVisibleTo("RuAgent.Broker.Tests")]
[assembly: InternalsVisibleTo("RuBackend.Tests")]

namespace RuAgent.Broker.Tools.Desktop;

public static class EndpointProof
{
    public static string Hash(string endpointId, ReadOnlySpan<byte> salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (salt.IsEmpty)
        {
            throw new ArgumentException("Endpoint proof salt is required.", nameof(salt));
        }

        var id = Encoding.UTF8.GetBytes(endpointId);
        var input = new byte[salt.Length + id.Length];
        salt.CopyTo(input);
        id.CopyTo(input.AsSpan(salt.Length));
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant()[..16];
    }
}

internal interface ICoreAudioNativeApi
{
    int InitializeCom();
    object CreateEnumerator();
    object GetDefaultRenderEndpoint(object enumerator);
    object ActivateEndpointVolume(object device);
    string GetEndpointId(object device);
    VolumeState GetState(object endpoint);
    void SetLevel(object endpoint, int level);
    void SetMuted(object endpoint, bool muted);
    void Release(object resource);
    void UninitializeCom();
}

public sealed class WindowsCoreAudioVolumeController : IVolumeController
{
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private static readonly byte[] ProcessSalt = RandomNumberGenerator.GetBytes(32);

    private readonly ICoreAudioNativeApi _native;
    private readonly byte[] _salt;

    public WindowsCoreAudioVolumeController()
        : this(new CoreAudioNativeApi(), ProcessSalt)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new VolumeControllerException("unsupported_platform");
        }
    }

    internal WindowsCoreAudioVolumeController(ICoreAudioNativeApi native, ReadOnlySpan<byte> salt)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        if (salt.Length != 32)
        {
            throw new ArgumentException("Core Audio endpoint proof salt must be 32 bytes.", nameof(salt));
        }

        _salt = salt.ToArray();
    }

    public IVolumeEndpoint OpenDefaultRenderEndpoint()
    {
        object? enumerator = null;
        object? device = null;
        object? endpoint = null;
        var ownsComInitialization = false;
        var success = false;
        try
        {
            var initialization = _native.InitializeCom();
            ownsComInitialization = initialization is SOk or SFalse;
            if (!ownsComInitialization && initialization != RpcEChangedMode)
            {
                throw new VolumeControllerException("default_endpoint_unavailable");
            }

            enumerator = _native.CreateEnumerator();
            device = _native.GetDefaultRenderEndpoint(enumerator);
            endpoint = _native.ActivateEndpointVolume(device);
            var endpointHash = EndpointProof.Hash(_native.GetEndpointId(device), _salt);
            var lease = new CoreAudioEndpointLease(
                _native,
                enumerator,
                device,
                endpoint,
                endpointHash,
                ownsComInitialization,
                Environment.CurrentManagedThreadId);
            success = true;
            return lease;
        }
        catch (VolumeControllerException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new VolumeControllerException("default_endpoint_unavailable");
        }
        finally
        {
            if (!success)
            {
                ReleaseAcquisition(endpoint, device, enumerator, ownsComInitialization);
            }
        }
    }

    private void ReleaseAcquisition(
        object? endpoint,
        object? device,
        object? enumerator,
        bool ownsComInitialization)
    {
        try
        {
            if (endpoint is not null)
            {
                _native.Release(endpoint);
            }
        }
        finally
        {
            try
            {
                if (device is not null)
                {
                    _native.Release(device);
                }
            }
            finally
            {
                try
                {
                    if (enumerator is not null)
                    {
                        _native.Release(enumerator);
                    }
                }
                finally
                {
                    if (ownsComInitialization)
                    {
                        _native.UninitializeCom();
                    }
                }
            }
        }
    }

    private sealed class CoreAudioEndpointLease : IVolumeEndpoint
    {
        private readonly ICoreAudioNativeApi _native;
        private readonly object _enumerator;
        private readonly object _device;
        private readonly object _endpoint;
        private readonly bool _ownsComInitialization;
        private readonly int _ownerThreadId;
        private int _disposed;

        public CoreAudioEndpointLease(
            ICoreAudioNativeApi native,
            object enumerator,
            object device,
            object endpoint,
            string endpointHash,
            bool ownsComInitialization,
            int ownerThreadId)
        {
            _native = native;
            _enumerator = enumerator;
            _device = device;
            _endpoint = endpoint;
            EndpointHash = endpointHash;
            _ownsComInitialization = ownsComInitialization;
            _ownerThreadId = ownerThreadId;
        }

        public string EndpointRole => "default_render_multimedia";
        public string EndpointHash { get; }

        public VolumeState GetState()
        {
            ThrowIfDisposed();
            try
            {
                return _native.GetState(_endpoint);
            }
            catch (VolumeControllerException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new VolumeControllerException("read_failed");
            }
        }

        public void SetLevel(int level)
        {
            ThrowIfDisposed();
            try
            {
                _native.SetLevel(_endpoint, level);
            }
            catch (VolumeControllerException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new VolumeControllerException("write_failed");
            }
        }

        public void SetMuted(bool muted)
        {
            ThrowIfDisposed();
            try
            {
                _native.SetMuted(_endpoint, muted);
            }
            catch (VolumeControllerException)
            {
                throw;
            }
            catch (Exception)
            {
                throw new VolumeControllerException("write_failed");
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                throw new VolumeControllerException("endpoint_thread_mismatch");
            }

            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _native.Release(_endpoint);
            }
            finally
            {
                try
                {
                    _native.Release(_device);
                }
                finally
                {
                    try
                    {
                        _native.Release(_enumerator);
                    }
                    finally
                    {
                        if (_ownsComInitialization)
                        {
                            _native.UninitializeCom();
                        }
                    }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(CoreAudioEndpointLease));
            }
        }
    }
}

internal sealed class CoreAudioNativeApi : ICoreAudioNativeApi
{
    private const uint CoinitMultithreaded = 0x0;

    public int InitializeCom() => CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);

    public object CreateEnumerator()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new VolumeControllerException("unsupported_platform");
        }

        return (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
    }

    public object GetDefaultRenderEndpoint(object enumerator)
    {
        var nativeEnumerator = (IMMDeviceEnumerator)enumerator;
        ThrowIfFailed(
            nativeEnumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device),
            "default_endpoint_unavailable");
        return device;
    }

    public object ActivateEndpointVolume(object device)
    {
        var nativeDevice = (IMMDevice)device;
        var interfaceId = typeof(IAudioEndpointVolume).GUID;
        ThrowIfFailed(
            nativeDevice.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out var activated),
            "default_endpoint_unavailable");
        return activated;
    }

    public string GetEndpointId(object device)
    {
        ThrowIfFailed(((IMMDevice)device).GetId(out var endpointId), "default_endpoint_unavailable");
        return endpointId;
    }

    public VolumeState GetState(object endpoint)
    {
        var nativeEndpoint = (IAudioEndpointVolume)endpoint;
        ThrowIfFailed(nativeEndpoint.GetMasterVolumeLevelScalar(out var scalar), "read_failed");
        ThrowIfFailed(nativeEndpoint.GetMute(out var muted), "read_failed");
        var level = (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f, MidpointRounding.AwayFromZero);
        return new VolumeState(level, muted);
    }

    public void SetLevel(object endpoint, int level)
    {
        var context = Guid.Empty;
        ThrowIfFailed(
            ((IAudioEndpointVolume)endpoint).SetMasterVolumeLevelScalar(
                Math.Clamp(level, 0, 100) / 100f,
                ref context),
            "write_failed");
    }

    public void SetMuted(object endpoint, bool muted)
    {
        var context = Guid.Empty;
        ThrowIfFailed(((IAudioEndpointVolume)endpoint).SetMute(muted, ref context), "write_failed");
    }

    public void Release(object resource)
    {
        if (OperatingSystem.IsWindows() && Marshal.IsComObject(resource))
        {
            Marshal.FinalReleaseComObject(resource);
        }
    }

    public void UninitializeCom() => CoUninitialize();

    private static void ThrowIfFailed(int hResult, string code)
    {
        if (hResult < 0)
        {
            throw new VolumeControllerException(code);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [Flags]
    private enum ClsCtx
    {
        InprocServer = 0x1,
        InprocHandler = 0x2,
        LocalServer = 0x4,
        All = InprocServer | InprocHandler | LocalServer,
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out object devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice endpoint);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            ClsCtx context,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.Interface)] out object activated);

        [PreserveSig]
        int OpenPropertyStore(uint access, out object properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
        [PreserveSig] int VolumeStepUp(ref Guid eventContext);
        [PreserveSig] int VolumeStepDown(ref Guid eventContext);
        [PreserveSig] int QueryHardwareSupport(out uint hardwareSupportMask);
        [PreserveSig] int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }
}
