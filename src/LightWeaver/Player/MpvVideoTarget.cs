using System.Globalization;

namespace LightWeaver.Player;

/// <summary>Where mpv draws: into a child window it owns (the WPF app), or into a composition
/// swapchain the host attaches to a SwapChainPanel (the WinUI 3 app).</summary>
public abstract record MpvVideoTarget
{
    /// <summary>A child HWND; mpv probes its monitor for HDR and colour itself.</summary>
    public sealed record Window(nint Handle) : MpvVideoTarget;

    /// <summary>A composition swapchain of the given size in pixels. mpv cannot probe a display
    /// here, so <paramref name="Display"/> says what the window is on.</summary>
    public sealed record Composition(int Width, int Height, DisplayTarget Display) : MpvVideoTarget;
}

/// <summary>What the host knows about the display under the window. In composition mode mpv has no
/// window, so it cannot probe the output (video/out/d3d11/context.c returns an empty target colour
/// space when <c>opts.composition</c> is set); the host reads DXGI and passes it on.</summary>
/// <remarks><paramref name="MinNits"/> and <paramref name="Gamut"/> are the rest of what window mode
/// reads from DXGI (mp_dxgi_desc_to_color_space): the black level and the panel's own primaries as
/// "Rx,Ry,Gx,Gy,Bx,By,Wx,Wy". Without them mpv assumes a perfect black and a full BT.2020 panel,
/// which renders deeper and more saturated than the child-window build (spike W1, criterion 1).</remarks>
public sealed record DisplayTarget(bool Hdr, bool TenBit, float PeakNits, float SdrWhiteNits, float MinNits = 0, string? Gamut = null)
{
    public static DisplayTarget Sdr { get; } = new(false, false, 0, 0);

    /// <summary>The mpv options that recreate window mode's own display probe.</summary>
    public IReadOnlyList<(string Name, string Value)> MpvOptions()
    {
        var peak = Hdr ? PeakNits : 203f;
        var contrast = peak > 0 && MinNits > 0 ? Math.Clamp(peak / MinNits, 10, 1_000_000) : 0;
        return
        [
            ("d3d11-output-format", Hdr || TenBit ? "rgb10_a2" : "auto"),
            ("d3d11-output-csp", Hdr ? "pq" : "auto"),
            ("target-colorspace-hint", Hdr ? "yes" : "auto"),
            ("target-peak", Hdr && PeakNits > 0 ? PeakNits.ToString("0", CultureInfo.InvariantCulture) : "auto"),
            ("target-contrast", contrast > 0 ? contrast.ToString("0", CultureInfo.InvariantCulture) : "auto"),
            ("target-gamut", Gamut ?? "auto"),
        ];
    }
}
