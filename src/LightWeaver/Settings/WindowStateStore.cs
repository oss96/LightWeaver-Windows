using System.IO;
using System.Text.Json;
using System.Windows;

namespace LightWeaver.Settings;

/// <summary>Last window placement — machine state, kept out of settings.json.</summary>
public sealed class WindowStateData
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Persists window placement to %LOCALAPPDATA%\LightWeaver\window-state.json.</summary>
public static class WindowStateStore
{
    private static readonly string Path = System.IO.Path.Combine(AppPaths.Root, "window-state.json");

    /// <summary>Applies the saved placement. Positions that would land (mostly) off the
    /// current virtual screen are ignored — the monitor layout may have changed.</summary>
    public static void Restore(Window window)
    {
        WindowStateData? state;
        try
        {
            if (!File.Exists(Path))
                return;
            state = JsonSerializer.Deserialize<WindowStateData>(File.ReadAllText(Path));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return;
        }
        if (state is null || state.Width < 400 || state.Height < 300)
            return;

        window.Width = Math.Min(state.Width, SystemParameters.VirtualScreenWidth);
        window.Height = Math.Min(state.Height, SystemParameters.VirtualScreenHeight);

        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(new Rect(state.Left, state.Top, window.Width, window.Height), virtualScreen);
        if (!visible.IsEmpty && visible.Width >= 120 && visible.Height >= 80)
        {
            window.Left = state.Left;
            window.Top = state.Top;
        }
        if (state.Maximized)
            window.WindowState = WindowState.Maximized;
    }

    /// <summary>Saves the window's normal bounds. <paramref name="effectiveState"/> lets the
    /// caller substitute the pre-fullscreen state when closing from fullscreen.</summary>
    public static void Save(Window window, WindowState effectiveState)
    {
        // When maximized/minimized/fullscreen, the normal rect lives in RestoreBounds.
        var normal = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        if (normal.IsEmpty || normal.Width < 1 || normal.Height < 1)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(new WindowStateData
            {
                Left = normal.X,
                Top = normal.Y,
                Width = normal.Width,
                Height = normal.Height,
                Maximized = effectiveState == WindowState.Maximized,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // placement is a nicety; never block shutdown on it
        }
    }
}
