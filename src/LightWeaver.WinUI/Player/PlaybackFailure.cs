namespace LightWeaver.WinUI.Player;

/// <summary>Where the failed stream came from, which decides the message.</summary>
public enum FailureCause { LocalFile, Link, DirectPlay, Transcode }

/// <summary>The second action of the failure panel: whatever is likely to play instead.</summary>
public enum FailureAlternative { None, OriginalQuality, OtherVersion }

/// <summary>A title that could not be played (design 14b); <paramref name="Reason"/> is the server
/// or mpv error text shown under Details.</summary>
public sealed record PlaybackFailure(string Reason, FailureCause Cause, FailureAlternative Alternative);
