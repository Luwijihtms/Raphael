using System.Runtime.InteropServices;

namespace Raphael;

/// <summary>
/// Windows' own master volume and mute — the actual speaker level, the same one the volume keys and the taskbar slider
/// control — through the Core Audio API. Separate from Spotify's own volume (SpotifyClient) and from pausing or
/// resuming what is playing (MediaControl).
///
/// A PC can have several active playback devices at once (built-in speakers, HDMI, a headset, a virtual device such as
/// SteelSeries Sonar) with apps split across them, each with its own independent volume/mute in Windows. Muting only
/// "the default" one can silence Raphael's own voice while everything else keeps playing, so mute/unmute/set apply to
/// every active one; the reported level/percentage is read from the single default device, as that best answers "how
/// loud am I".
/// </summary>
public static class SystemVolume
{
    public static (int Percent, bool Muted) Get()
    {
        var ep = GetEndpoint(GetDefaultDevice());
        try
        {
            ep.GetMasterVolumeLevelScalar(out var level);
            ep.GetMute(out var muted);
            return ((int)Math.Round(level * 100), muted);
        }
        finally { Marshal.ReleaseComObject(ep); }
    }

    public static void SetVolume(int percent)
    {
        var level = Math.Clamp(percent, 0, 100) / 100f;
        ForEachActiveDevice(ep => ep.SetMasterVolumeLevelScalar(level, Guid.Empty));
    }

    public static void SetMute(bool mute) => ForEachActiveDevice(ep => ep.SetMute(mute, Guid.Empty));

    /// <summary>Level and mute state of every active playback device, not just the default one. Mainly for diagnostics/tests.</summary>
    public static List<(int Percent, bool Muted)> GetAll()
    {
        var states = new List<(int, bool)>();
        foreach (var device in GetActiveDevices())
        {
            var ep = GetEndpoint(device);
            try
            {
                ep.GetMasterVolumeLevelScalar(out var level);
                ep.GetMute(out var muted);
                states.Add(((int)Math.Round(level * 100), muted));
            }
            finally { Marshal.ReleaseComObject(ep); Marshal.ReleaseComObject(device); }
        }
        return states;
    }

    /// <summary>
    /// Applies <paramref name="action"/> to every active device. One device misbehaving (some virtual/driver devices,
    /// e.g. SteelSeries Sonar's, can refuse a Set call) is skipped rather than aborting the rest.
    /// </summary>
    private static void ForEachActiveDevice(Action<IAudioEndpointVolume> action)
    {
        foreach (var device in GetActiveDevices())
        {
            IAudioEndpointVolume? ep = null;
            try { ep = GetEndpoint(device); action(ep); }
            catch { /* that one device is skipped; the others still get it */ }
            finally
            {
                if (ep != null) Marshal.ReleaseComObject(ep);
                Marshal.ReleaseComObject(device);
            }
        }
    }

    private static IMMDevice GetDefaultDevice()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.GetDefaultAudioEndpoint(/* eRender */ 0, /* eMultimedia */ 1, out var device);
            return device;
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    /// <summary>Every currently active playback device (speakers, HDMI, a headset, a virtual device, ...), not just the default one.</summary>
    private static List<IMMDevice> GetActiveDevices()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            enumerator.EnumAudioEndpoints(/* eRender */ 0, /* DEVICE_STATE_ACTIVE */ 1, out var collection);
            try
            {
                collection.GetCount(out var count);
                var devices = new List<IMMDevice>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    collection.Item(i, out var device);
                    devices.Add(device);
                }
                return devices;
            }
            finally { Marshal.ReleaseComObject(collection); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }

    private static IAudioEndpointVolume GetEndpoint(IMMDevice device)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, /* CLSCTX_ALL */ 23, IntPtr.Zero, out var ep);
        return (IAudioEndpointVolume)ep;
    }

    // ---- the small slice of Windows' Core Audio COM interfaces this needs (mmdeviceapi.h / endpointvolume.h) ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out uint count);
        int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpointVolume);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, Guid eventContext);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute(bool mute, Guid eventContext);
        int GetMute(out bool mute);
    }
}
