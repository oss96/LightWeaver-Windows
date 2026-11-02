namespace LightWeaver.Jellyfin;

public enum TransportKind
{
    Pause, Unpause, TogglePause, SeekAbsolute, SeekRelative, FrameStep, Speed, Stop,
    QueueJump, QueueNext, QueuePrevious, QueueRemove,
}

public sealed record TransportRequest(TransportKind Kind, double Seconds = 0, Guid? PlaylistItemId = null);
public enum GateOutcome { Apply, Post, Drop }
public sealed record GateDecision(GateOutcome Outcome, string? Verb = null, long PositionTicks = 0,
    string? Reason = null, Guid? PlaylistItemId = null);

/// <summary>Local transport becomes a group request; only the server echo changes playback.</summary>
public static class SyncPlayGate
{
    public static GateDecision Resolve(TransportRequest request, bool groupPlayback, bool isPaused,
        double positionSeconds, double durationSeconds)
    {
        if (!groupPlayback) return new(GateOutcome.Apply);
        switch (request.Kind)
        {
            case TransportKind.Pause: return new(GateOutcome.Post, "pause");
            case TransportKind.Unpause: return new(GateOutcome.Post, "unpause");
            case TransportKind.TogglePause: return new(GateOutcome.Post, isPaused ? "unpause" : "pause");
            case TransportKind.Stop: return new(GateOutcome.Post, "stop");
            case TransportKind.SeekAbsolute:
            case TransportKind.SeekRelative:
                if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
                    return new(GateOutcome.Drop, Reason: "no_duration");
                var seconds = request.Kind == TransportKind.SeekRelative
                    ? positionSeconds + request.Seconds : request.Seconds;
                if (!double.IsFinite(seconds)) return new(GateOutcome.Drop, Reason: "invalid_position");
                return new(GateOutcome.Post, "seek", (long)(Math.Clamp(seconds, 0,
                    Math.Min(durationSeconds, long.MaxValue / (double)TimeSpan.TicksPerSecond)) * TimeSpan.TicksPerSecond));
            case TransportKind.QueueNext: return new(GateOutcome.Post, "next");
            case TransportKind.QueuePrevious: return new(GateOutcome.Post, "previous");
            case TransportKind.QueueJump:
            case TransportKind.QueueRemove:
                return request.PlaylistItemId is { } id && id != Guid.Empty
                    ? new(GateOutcome.Post, request.Kind == TransportKind.QueueJump ? "select" : "remove", PlaylistItemId: id)
                    : new(GateOutcome.Drop, Reason: "queue");
            case TransportKind.FrameStep: return new(GateOutcome.Drop, Reason: "frame_step");
            default: return new(GateOutcome.Drop, Reason: "speed");
        }
    }
}
