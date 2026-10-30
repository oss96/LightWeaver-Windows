using LightWeaver.Jellyfin;
using LightWeaver.Player;
using LightWeaver.Settings;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name);
    checks++;
}

// The Oct 29 case: the group plays from 6000 s, this member stalls for 2.95 s at 6100 s, the server
// waits for it, and the group plays on from where it stalled.
var entry = Guid.NewGuid();
var start = new DateTime(2026, 10, 29, 20, 0, 0, DateTimeKind.Utc);
var unpause = new SyncPlayCommandMessage(Guid.NewGuid(), entry, new DateTimeOffset(start), TimeSpan.FromSeconds(6000).Ticks,
    SyncPlayCommandTypes.Unpause, new DateTimeOffset(start));
var stalledAt = start.AddSeconds(100);
var resumedAt = stalledAt.AddSeconds(2.95);
SyncPlayState Playing(string? groupState, SyncPlayDriftAnchor? anchor = null) => new(start.AddMinutes(-1), entry,
    IsPaused: false, LastApplied: unpause, GroupState: groupState, DriftAnchor: anchor);
SyncPlayActionKind Drift(SyncPlayState state, DateTime now, double position)
    => SyncPlayScheduler.ResolveDrift(state, TimeSpan.Zero, now, position).Kind;

// --- Unchanged behaviour ------------------------------------------------------------------------------
Check(Drift(Playing(SyncPlayGroupStates.Playing), start.AddSeconds(100), 6100) == SyncPlayActionKind.Noop,
    "in position while the group plays: no correction");
Check(Drift(Playing(SyncPlayGroupStates.Playing), start.AddSeconds(100), 6098) == SyncPlayActionKind.DriftSeek,
    "2 s behind while the group plays: a drift seek");
Check(Drift(Playing(null), start.AddSeconds(100), 6098) == SyncPlayActionKind.DriftSeek,
    "before the first state update the last Unpause still counts");

// --- The group waits for this member --------------------------------------------------------------
Check(SyncPlayScheduler.InterruptsUnpause(SyncPlayGroupStates.Waiting, unpause),
    "Waiting after an Unpause interrupts it");
Check(!SyncPlayScheduler.InterruptsUnpause(SyncPlayGroupStates.Waiting, unpause with { Command = SyncPlayCommandTypes.Pause }),
    "Waiting after a Pause interrupts nothing");
Check(!SyncPlayScheduler.InterruptsUnpause(SyncPlayGroupStates.Playing, unpause),
    "Playing interrupts nothing");
Check(Drift(Playing(SyncPlayGroupStates.Waiting), resumedAt.AddMilliseconds(3), 6100) == SyncPlayActionKind.Noop,
    "buffering just ended while the group still waits: no jump ahead by the stall");
Check(Drift(Playing(SyncPlayGroupStates.Paused), resumedAt, 6100) == SyncPlayActionKind.Noop,
    "a paused group is no reference either");

// --- The group plays again without an Unpause for this member --------------------------------------
var anchor = SyncPlayScheduler.ResumeAnchor(true, SyncPlayGroupStates.Playing, resumedAt, 6100);
Check(anchor == new SyncPlayDriftAnchor(resumedAt, TimeSpan.FromSeconds(6100).Ticks),
    "the resume anchors the group at this member's position and the resume instant");
Check(SyncPlayScheduler.ResumeAnchor(false, SyncPlayGroupStates.Playing, resumedAt, 6100) is null,
    "a Playing update after an uninterrupted Unpause sets no anchor");
Check(SyncPlayScheduler.ResumeAnchor(true, SyncPlayGroupStates.Waiting, resumedAt, 6100) is null,
    "still waiting: no anchor yet");
Check(Drift(Playing(SyncPlayGroupStates.Playing), resumedAt.AddMilliseconds(130), 6100.13) == SyncPlayActionKind.DriftSeek,
    "without the anchor the old Unpause would still seek it ahead by the stall");
Check(Drift(Playing(SyncPlayGroupStates.Playing, anchor), resumedAt.AddMilliseconds(130), 6100.13) == SyncPlayActionKind.Noop,
    "with the anchor the resumed member is in position");
var later = SyncPlayScheduler.ResolveDrift(Playing(SyncPlayGroupStates.Playing, anchor), TimeSpan.Zero, resumedAt.AddSeconds(30), 6128);
Check(later.Kind == SyncPlayActionKind.DriftSeek && later.TargetSeconds == 6130,
    "real drift after the resume is measured from the anchor");

// --- A drift seek on a transcode lands where the group will be ----------------------------------
SyncPlayState Leading(TimeSpan lead) => Playing(SyncPlayGroupStates.Playing) with { DriftSeekLead = lead };
var plain = SyncPlayScheduler.ResolveDrift(Leading(TimeSpan.Zero), TimeSpan.Zero, start.AddSeconds(100), 6098);
var led = SyncPlayScheduler.ResolveDrift(Leading(TimeSpan.FromSeconds(1.5)), TimeSpan.Zero, start.AddSeconds(100), 6098);
Check(plain.TargetSeconds == 6100 && led.TargetSeconds == 6101.5,
    "the last seek's 1.5 s to play again is added to the next target");
Check(SyncPlayScheduler.ResolveDrift(Leading(TimeSpan.FromSeconds(8)), TimeSpan.Zero, start.AddSeconds(100), 6098).TargetSeconds == 6103,
    "a seek that stalled on the network leads by at most 3 s");
Check(Drift(Leading(TimeSpan.FromSeconds(1.5)), start.AddSeconds(100), 6100) == SyncPlayActionKind.Noop,
    "the lead moves the target, not the decision: in position stays put");

// --- The audio-sync repair reads a group seek as a seek, not as playing video --------------------
Check(MpvPlayer.VideoPlayedSince(10, 13, paused: false, seeksAtArm: 4, seeksNow: 4), "three seconds played: the check may judge audio");
Check(!MpvPlayer.VideoPlayedSince(0.04, 22.31, paused: true, seeksAtArm: 4, seeksNow: 5),
    "the Oct 29 case: a paused group seek from 0 to 22.3 s is not playback");
Check(!MpvPlayer.VideoPlayedSince(10, 22.31, paused: false, seeksAtArm: 4, seeksNow: 5), "a seek while playing is not playback either");
Check(!MpvPlayer.VideoPlayedSince(10, 13, paused: true, seeksAtArm: 4, seeksNow: 4), "a paused player is not playing");
Check(!MpvPlayer.VideoPlayedSince(10, 10.2, paused: false, seeksAtArm: 4, seeksNow: 4), "under half a second is too little to judge");
Check(!MpvPlayer.VideoPlayedSince(double.NaN, 13, paused: false, seeksAtArm: 4, seeksNow: 4), "no position at arm: no judgement");

// --- Normalization stays out of a group's item ---------------------------------------------------
var normalize = new AppSettings { VolumeNormalization = true };
Check(MpvPlayer.NormalizesAudio(normalize, groupPlayback: false), "normalization on alone: dynaudnorm");
Check(!MpvPlayer.NormalizesAudio(normalize, groupPlayback: true), "normalization on in a group: no dynaudnorm, so no 16 s hold");
Check(!MpvPlayer.NormalizesAudio(new AppSettings { VolumeNormalization = true, AudioPassthrough = true }, groupPlayback: false),
    "passthrough still wins over normalization");
Check(!MpvPlayer.NormalizesAudio(new AppSettings(), groupPlayback: false), "normalization off: no dynaudnorm");

Console.WriteLine($"{checks} checks passed");
return 0;
