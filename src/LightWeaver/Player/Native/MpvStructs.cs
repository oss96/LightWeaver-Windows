using System.Runtime.InteropServices;

namespace LightWeaver.Player.Native;

/// <summary>Mirrors mpv_event (client.h). Returned by mpv_wait_event as a pointer into mpv-owned memory —
/// valid only until the next mpv_wait_event call on the same handle.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public nint Data; // points at the event-specific struct below, or null
}

/// <summary>Mirrors mpv_event_property (client.h). Data payload of MpvEventId.PropertyChange.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public nint Name;      // const char* (UTF-8, mpv-owned)
    public MpvFormat Format;
    public nint Data;      // double*/int*/char** per Format; null if property unavailable
}

/// <summary>Mirrors mpv_event_log_message (client.h). Data payload of MpvEventId.LogMessage.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public nint Prefix;    // const char*
    public nint Level;     // const char*
    public nint Text;      // const char*
    public int LogLevel;
}

/// <summary>Mirrors mpv_event_end_file (client.h). Data payload of MpvEventId.EndFile.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;      // meaningful when Reason == Error
    public long PlaylistEntryId;
}
