using System.Runtime.InteropServices;

namespace LightWeaver.Player;

/// <summary>Keeps the display and the system awake while video plays. mpv does this itself only
/// when it owns a window, so the composition player holds it here.</summary>
internal sealed class KeepAwake : IDisposable
{
    private readonly IPowerRequest _request;
    private readonly Lock _lock = new();
    private bool _held;
    private bool _disposed;

    public KeepAwake() : this(new WindowsPowerRequest("LightWeaver is playing a video")) { }

    internal KeepAwake(IPowerRequest request) => _request = request;

    /// <summary>Whether Windows was last told to stay awake.</summary>
    public bool IsHeld
    {
        get { lock (_lock) return _held; }
    }

    /// <summary>Holds or releases the request; asking for the state it is in does nothing.</summary>
    public void Hold(bool awake)
    {
        lock (_lock)
        {
            if (_disposed || awake == _held)
                return;
            if (awake)
                _held = _request.Set();
            else
            {
                _request.Clear();
                _held = false;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_held)
                _request.Clear();
            _held = false;
            _request.Dispose();
        }
    }
}

/// <summary>A counted Windows power request: every successful <see cref="Set"/> needs one
/// <see cref="Clear"/>.</summary>
internal interface IPowerRequest : IDisposable
{
    bool Set();
    void Clear();
}

/// <summary>A Windows power request for the display and the system, listed by
/// <c>powercfg /requests</c> under the given reason.</summary>
internal sealed partial class WindowsPowerRequest : IPowerRequest
{
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 1;

    private readonly nint _handle;

    public WindowsPowerRequest(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new ReasonContext
            {
                Version = PowerRequestContextVersion,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = text,
            };
            var handle = PowerCreateRequest(ref context);
            _handle = handle == -1 ? 0 : handle;
        }
        finally
        {
            Marshal.FreeHGlobal(text);
        }
    }

    public bool Set()
    {
        if (_handle == 0 || !PowerSetRequest(_handle, PowerRequestDisplayRequired))
            return false;
        if (PowerSetRequest(_handle, PowerRequestSystemRequired))
            return true;
        PowerClearRequest(_handle, PowerRequestDisplayRequired);
        return false;
    }

    public void Clear()
    {
        if (_handle == 0)
            return;
        PowerClearRequest(_handle, PowerRequestSystemRequired);
        PowerClearRequest(_handle, PowerRequestDisplayRequired);
    }

    public void Dispose()
    {
        if (_handle != 0)
            CloseHandle(_handle);
    }

    /// <summary>REASON_CONTEXT, sized to its x64 union of the simple string and the detailed form.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 32)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
    }

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint handle, int requestType);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint handle, int requestType);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
