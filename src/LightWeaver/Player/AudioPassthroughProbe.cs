using System.IO;
using System.Runtime.InteropServices;

namespace LightWeaver.Player;

/// <summary>One bitstream format and whether the probed device accepts it.</summary>
public sealed record PassthroughFormat(string Label, bool Supported);

/// <summary>What a probe concluded about one endpoint.</summary>
public enum PassthroughVerdict
{
    /// <summary>The endpoint could not be examined — no claim is made about it.</summary>
    Unknown,
    /// <summary>It rejects exclusive mode outright, which passthrough requires. Virtual
    /// devices (audio enhancers, streaming endpoints) behave this way.</summary>
    NoExclusiveMode,
    /// <summary>It answers, and accepts none of the bitstream formats.</summary>
    NoneSupported,
    /// <summary>It accepts at least one.</summary>
    SomeSupported,
}

/// <summary>A probe result: the verdict, the endpoint it is about, and the per-format detail.</summary>
public sealed record PassthroughProbe(PassthroughVerdict Verdict, string DeviceName,
    IReadOnlyList<PassthroughFormat> Formats);

/// <summary>
/// Asks a WASAPI endpoint, up front, which IEC 61937 bitstream formats it will accept in
/// exclusive mode — so the Settings passthrough toggle can say whether it can do anything at
/// all on the selected output.
///
/// This is a genuine query, not a guess: <c>IAudioClient::IsFormatSupported</c> with
/// <c>AUDCLNT_SHAREMODE_EXCLUSIVE</c> neither opens nor takes the device, so probing does not
/// interrupt whatever is playing. It is the same question mpv's own WASAPI output asks
/// ("Trying 7.1 spdif-dtshd (16/16 bits) @ 192000hz (exclusive) -&gt; unsupported"), which is why
/// its answer matches what playback will do.
///
/// Everything here is best-effort: a driver that misreports, a device held exclusively by
/// another process, or a machine with no endpoint at all yields <c>null</c> rather than an
/// exception, and the caller shows nothing. A probe that lies is worse than no probe, so the
/// runtime fallback in <see cref="MpvPlayer"/> remains the real safety net — this only informs.
/// </summary>
public static class AudioPassthroughProbe
{
    // The five codecs LightWeaver asks mpv to bitstream (audio-spdif), with the format each
    // one is actually sent as. Rates/channel counts match ao_wasapi's spdif setup - probing a
    // format nobody will request would answer the wrong question.
    private static readonly (string Label, Guid SubFormat, int Rate, int Channels)[] Formats =
    [
        ("AC3",     new Guid("00000092-0000-0010-8000-00aa00389b71"),  48000, 2),
        ("E-AC3",   new Guid("0000000a-0cea-0010-8000-00aa00389b71"), 192000, 2),
        ("DTS",     new Guid("00000008-0000-0010-8000-00aa00389b71"),  48000, 2),
        ("DTS-HD",  new Guid("0000000b-0cea-0010-8000-00aa00389b71"), 192000, 8),
        ("TrueHD",  new Guid("0000000c-0cea-0010-8000-00aa00389b71"), 192000, 8),
    ];

    /// <summary>Ordinary 16-bit PCM — the control format for the plumbing self-check.</summary>
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>One exclusive-mode format question. closestMatch must be NULL in exclusive mode;
    /// S_OK means the endpoint takes the format, any failure HRESULT (normally
    /// AUDCLNT_E_UNSUPPORTED_FORMAT) means it does not.</summary>
    private static bool Accepts(IAudioClient client, Guid subFormat, int rate, int channels, int bits = 16)
    {
        var handle = GCHandle.Alloc(BuildFormat(subFormat, rate, channels, bits), GCHandleType.Pinned);
        try { return client.IsFormatSupported(1, handle.AddrOfPinnedObject(), nint.Zero) == 0; }
        catch { return false; }
        finally { handle.Free(); }
    }

    /// <summary>Friendly name of the default render endpoint, or null if it cannot be read.</summary>
    public static string? DefaultDeviceName()
    {
        try
        {
            var device = GetDevice(null);
            return device is null ? null : FriendlyName(device);
        }
        catch { return null; }
    }

    /// <summary>
    /// Probes one endpoint. <paramref name="mpvDeviceName"/> is an mpv <c>audio-device</c> value
    /// ("auto", or "wasapi/{0.0.0.00000000}.{guid}"); "auto" and null probe the default device,
    /// which is what mpv will pick. Returns null when the endpoint cannot be examined at all.
    /// </summary>
    public static PassthroughProbe Probe(string? mpvDeviceName)
    {
        var name = "This output";
        try
        {
            var device = GetDevice(mpvDeviceName);
            if (device is null)
                return Unknown(name);
            name = FriendlyName(device) ?? name;
            var iid = typeof(IAudioClient).GUID;
            // CLSCTX_ALL. Activate does not start a stream - it only creates the client object.
            if (device.Activate(ref iid, 23, nint.Zero, out var raw) != 0 || raw == nint.Zero)
                return Unknown(name);
            var client = (IAudioClient)Marshal.GetObjectForIUnknown(raw);
            Marshal.Release(raw);
            try
            {
                // Control leg, and it gates everything below. A bare "not supported" is
                // indistinguishable from a wrong format blob or a wrong vtable slot — and on a
                // machine whose outputs are all analog or virtual (the common case) EVERY answer
                // is negative, so no amount of testing here would catch such a bug. Plain PCM
                // through the IDENTICAL call is the control. Measured on this hardware: it passes
                // on the Realtek and Steam endpoints (which then genuinely reject every bitstream
                // format) and fails on the FxSound enhancer and a USB DAC — so it discriminates.
                if (!Accepts(client, PcmSubFormat, 48000, 2)
                    && !Accepts(client, PcmSubFormat, 48000, 2, bits: 24)
                    && !Accepts(client, PcmSubFormat, 44100, 2))
                {
                    // Not "unknown": passthrough is only ever attempted in exclusive mode, so an
                    // endpoint that will not grant exclusive mode for ordinary PCM cannot
                    // bitstream either. This is the verdict for a virtual device.
                    return new PassthroughProbe(PassthroughVerdict.NoExclusiveMode, name, []);
                }

                var results = new List<PassthroughFormat>();
                foreach (var (label, sub, rate, channels) in Formats)
                    results.Add(new PassthroughFormat(label, Accepts(client, sub, rate, channels)));
                return new PassthroughProbe(
                    results.Any(r => r.Supported) ? PassthroughVerdict.SomeSupported : PassthroughVerdict.NoneSupported,
                    name, results);
            }
            finally { Marshal.ReleaseComObject(client); }
        }
        catch { return Unknown(name); }
    }

    private static PassthroughProbe Unknown(string name)
        => new(PassthroughVerdict.Unknown, name, []);

    /// <summary>The Settings line, or null when nothing can honestly be said.</summary>
    public static string? Describe(string? mpvDeviceName) => Describe(Probe(mpvDeviceName));

    /// <summary>Overload for callers that already hold the probe — the verdict also drives the
    /// row's colour, and probing twice would double the COM round trips for one row.</summary>
    public static string? Describe(PassthroughProbe probe)
    {
        return probe.Verdict switch
        {
            PassthroughVerdict.SomeSupported =>
                $"{probe.DeviceName} accepts: {string.Join(", ", probe.Formats.Where(f => f.Supported).Select(f => f.Label))}.",
            PassthroughVerdict.NoneSupported =>
                $"{probe.DeviceName} accepts none of these formats — playback falls back to decoded audio.",
            PassthroughVerdict.NoExclusiveMode =>
                $"{probe.DeviceName} does not allow exclusive-mode output, which passthrough requires — playback falls back to decoded audio.",
            _ => null,
        };
    }

    // ---- COM plumbing ----

    private static IMMDevice? GetDevice(string? mpvDeviceName)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("bcde0395-e52f-467c-8e3d-c4579291692e"))!)!;
        // eRender = 0, eConsole = 0, DEVICE_STATE_ACTIVE = 1.
        if (mpvDeviceName is null or "auto" || !mpvDeviceName.StartsWith("wasapi/", StringComparison.Ordinal))
            return enumerator.GetDefaultAudioEndpoint(0, 0, out var def) == 0 ? def : null;

        // mpv spells a WASAPI device as "wasapi/" + the endpoint's own ID string, so the match
        // is exact rather than by name - two identical sound cards would otherwise be one entry.
        var wanted = mpvDeviceName["wasapi/".Length..];
        if (enumerator.EnumAudioEndpoints(0, 1, out var collection) != 0)
            return null;
        if (collection.GetCount(out var count) != 0)
            return null;
        for (var i = 0; i < count; i++)
        {
            if (collection.Item(i, out var dev) != 0)
                continue;
            if (dev.GetId(out var id) == 0 && string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase))
                return dev;
        }
        return null;
    }

    private static string? FriendlyName(IMMDevice device)
    {
        // STGM_READ = 0; PKEY_Device_FriendlyName.
        if (device.OpenPropertyStore(0, out var store) != 0)
            return null;
        var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
        if (store.GetValue(ref key, out var value) != 0)
            return null;
        try
        {
            // VT_LPWSTR = 31; anything else is not a name we can use.
            return value.Vt == 31 && value.Pointer != nint.Zero ? Marshal.PtrToStringUni(value.Pointer) : null;
        }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>WAVEFORMATEXTENSIBLE, laid out by hand — the managed struct marshallers do not
    /// give the exact 40-byte blob WASAPI expects.</summary>
    private static byte[] BuildFormat(Guid subFormat, int rate, int channels, int bits = 16)
    {
        var blockAlign = channels * bits / 8;
        var buf = new byte[40];
        var w = new BinaryWriter(new MemoryStream(buf));
        w.Write((ushort)0xFFFE);            // wFormatTag = WAVE_FORMAT_EXTENSIBLE
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * blockAlign);         // nAvgBytesPerSec
        w.Write((ushort)blockAlign);
        w.Write((ushort)bits);
        w.Write((ushort)22);                // cbSize
        w.Write((ushort)bits);              // Samples.wValidBitsPerSample
        w.Write(channels == 8 ? 0x63F : 0x3);  // dwChannelMask: 7.1, else stereo
        w.Write(subFormat.ToByteArray());
        return buf;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1, Reserved2, Reserved3;
        public nint Pointer;
        public nint Padding;
    }

    [ComImport, Guid("a95664d2-9614-4f35-a746-de8db63617e6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("d666063f-1587-4e43-81f1-b948e807363f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, out nint iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    /// <summary>Only IsFormatSupported is called. The preceding slots still have to be declared
    /// so the vtable offsets line up; their signatures are irrelevant because they are never
    /// invoked, and naming them for their real methods keeps that legible.</summary>
    [ComImport, Guid("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize();
        [PreserveSig] int GetBufferSize();
        [PreserveSig] int GetStreamLatency();
        [PreserveSig] int GetCurrentPadding();
        [PreserveSig] int IsFormatSupported(int shareMode, nint format, nint closestMatch);
    }
}
