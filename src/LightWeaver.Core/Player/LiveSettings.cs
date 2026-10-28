using LightWeaver.Settings;

namespace LightWeaver.Player;

/// <summary>The groups of settings a running player takes live, one per call that applies them.</summary>
[Flags]
public enum LiveSettingGroups
{
    None = 0,
    SubtitleStyle = 1,
    Audio = 2,
    VideoBuffer = 4,
    HardwareDecoding = 8,
}

/// <summary>The live settings a player was given, so a settings save re-applies only the groups it
/// changed: re-applying audio resets the normalization filter and re-arms passthrough recovery.</summary>
public sealed record LiveSettings(
    (string Font, int Size, string Color, string BorderColor, double BorderSize, double ShadowOffset) SubtitleStyle,
    (string Device, bool Passthrough, bool Normalization) Audio,
    int VideoBufferMiB,
    bool HardwareDecoding)
{
    public static LiveSettings Of(AppSettings settings) => new(
        (settings.SubtitleFontFamily, settings.SubtitleFontSize, settings.SubtitleTextColor, settings.SubtitleBorderColor, settings.SubtitleBorderSize, settings.SubtitleShadowOffset),
        (settings.AudioDevice, settings.AudioPassthrough, settings.VolumeNormalization),
        settings.VideoBufferMiB,
        settings.HardwareDecoding);

    /// <summary>Groups whose last apply failed, perhaps halfway: the player's state is unknown, so the
    /// next save applies them whatever their values.</summary>
    public LiveSettingGroups Dirty { get; init; }

    private static readonly LiveSettingGroups[] Groups =
        [LiveSettingGroups.SubtitleStyle, LiveSettingGroups.Audio, LiveSettingGroups.VideoBuffer, LiveSettingGroups.HardwareDecoding];

    /// <summary>Applies each group of <paramref name="next"/> that differs from this snapshot or is
    /// <see cref="Dirty"/>, and returns what is applied afterwards. A group whose <paramref name="apply"/>
    /// returns false or throws <see cref="MpvException"/> (reported to <paramref name="failed"/>) becomes
    /// dirty, so the next save tries it again; the other groups still apply.</summary>
    public LiveSettings ApplyChanges(LiveSettings next, Func<LiveSettingGroups, bool> apply, Action<LiveSettingGroups, MpvException>? failed = null)
    {
        var due = next.ChangedFrom(this) | Dirty;
        var succeeded = LiveSettingGroups.None;
        foreach (var group in Groups.Where(g => due.HasFlag(g)))
        {
            try
            {
                if (apply(group)) succeeded |= group;
            }
            catch (MpvException ex)
            {
                failed?.Invoke(group, ex);
            }
        }
        return new(
            succeeded.HasFlag(LiveSettingGroups.SubtitleStyle) ? next.SubtitleStyle : SubtitleStyle,
            succeeded.HasFlag(LiveSettingGroups.Audio) ? next.Audio : Audio,
            succeeded.HasFlag(LiveSettingGroups.VideoBuffer) ? next.VideoBufferMiB : VideoBufferMiB,
            succeeded.HasFlag(LiveSettingGroups.HardwareDecoding) ? next.HardwareDecoding : HardwareDecoding)
        {
            Dirty = due & ~succeeded,
        };
    }

    /// <summary>The groups that differ from <paramref name="applied"/>.</summary>
    public LiveSettingGroups ChangedFrom(LiveSettings applied)
        => (SubtitleStyle != applied.SubtitleStyle ? LiveSettingGroups.SubtitleStyle : LiveSettingGroups.None)
            | (Audio != applied.Audio ? LiveSettingGroups.Audio : LiveSettingGroups.None)
            | (VideoBufferMiB != applied.VideoBufferMiB ? LiveSettingGroups.VideoBuffer : LiveSettingGroups.None)
            | (HardwareDecoding != applied.HardwareDecoding ? LiveSettingGroups.HardwareDecoding : LiveSettingGroups.None);
}
