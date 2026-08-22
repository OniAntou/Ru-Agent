using System.Runtime.InteropServices;

namespace RuAgent.Broker.Tools.Desktop;

public sealed class WindowsCoreAudioVolumeController : IVolumeController
{
    private readonly IAudioEndpointVolume _endpoint;

    public WindowsCoreAudioVolumeController()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new VolumeControllerException("unsupported_platform");
        }

        try
        {
            var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
            ThrowIfFailed(
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device),
                "default_endpoint_unavailable");
            var interfaceId = typeof(IAudioEndpointVolume).GUID;
            ThrowIfFailed(
                device.Activate(ref interfaceId, ClsCtx.All, IntPtr.Zero, out var activated),
                "default_endpoint_unavailable");
            _endpoint = (IAudioEndpointVolume)activated;
        }
        catch (VolumeControllerException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new VolumeControllerException("default_endpoint_unavailable");
        }
    }

    public VolumeState GetState()
    {
        try
        {
            ThrowIfFailed(_endpoint.GetMasterVolumeLevelScalar(out var scalar), "read_failed");
            ThrowIfFailed(_endpoint.GetMute(out var muted), "read_failed");
            var level = (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f, MidpointRounding.AwayFromZero);
            return new VolumeState(level, muted);
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
        try
        {
            var context = Guid.Empty;
            ThrowIfFailed(
                _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, ref context),
                "write_failed");
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
        try
        {
            var context = Guid.Empty;
            ThrowIfFailed(_endpoint.SetMute(muted, ref context), "write_failed");
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

    private static void ThrowIfFailed(int hResult, string code)
    {
        if (hResult < 0)
        {
            throw new VolumeControllerException(code);
        }
    }

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
