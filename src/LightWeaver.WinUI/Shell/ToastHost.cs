using LightWeaver.Localization;
using LightWeaver.WinUI.Controls;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace LightWeaver.WinUI.Shell;

public enum ToastKind { Info, Success, Error, Download }

/// <summary>Toasts (design 14c): bottom centre, stacking upward, at most three (the oldest drops).
/// Info shows 4 s, or 6 s with an action; success may only offer Undo; errors stay until dismissed
/// or retried; a download that started or was queued shows the download arrow and lasts as info
/// does. Hovering pauses the timer. They fade in and out in 120 ms. A toast with the merge key of
/// one on screen replaces it in place; over video, toasts without an action show as compact toasts
/// on player glass. A lasting toast, such as an update's download progress, stays until dismissed or
/// removed, is never the one that drops, and never goes over video. A toast never takes focus on its own.</summary>
public sealed partial class ToastHost : StackPanel
{
    private const int MaxToasts = 3;
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(120));
    private readonly HashSet<UIElement> _lasting = [];

    public ToastHost()
    {
        Spacing = 8;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 24);
        AutomationProperties.SetAutomationId(this, "Toasts");
    }

    /// <summary>The window's host, so code anywhere can raise a toast.</summary>
    public static ToastHost? Current { get; set; }

    /// <summary>Shows the toast over video instead, when the player is full; false otherwise.</summary>
    public static Func<ToastKind, string, string?, bool>? OverVideo { get; set; }

    /// <summary>The user closed the toast with this merge key.</summary>
    public static event Action<string>? Dismissed;

    /// <summary>The glyph and its colour that mark a toast of this kind, here and over video.</summary>
    public static (string Glyph, Color Color) Mark(ToastKind kind) => kind switch
    {
        ToastKind.Success => (Theme.Icons.SuccessSolid, Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F)),
        ToastKind.Error => (Theme.Icons.ErrorSolid, Color.FromArgb(0xFF, 0xFF, 0x99, 0xA4)),
        ToastKind.Download => (Theme.Icons.Download, Color.FromArgb(0xFF, 0xF0, 0x8C, 0x9C)),
        _ => (Theme.Icons.InfoSolid, Color.FromArgb(0xFF, 0xF0, 0x8C, 0x9C)),
    };

    public static void Show(ToastKind kind, string message, string? action = null, Action? onAction = null, string? mergeKey = null)
    {
        if (action is null && OverVideo?.Invoke(kind, message, mergeKey) == true) return;
        Current?.Add(kind, message, action, onAction, mergeKey);
    }

    /// <summary>Shows a lasting toast, or changes only the text of the one with this merge key.</summary>
    public static void ShowLasting(ToastKind kind, string message, string mergeKey)
    {
        if (Current is not { } host) return;
        if (host.Find(mergeKey) is { Child: StackPanel row } toast && row.Children[1] is TextBlock text)
        {
            if (text.Text == message) return;
            text.Text = message;
            AutomationProperties.SetName(toast, message);
            return;
        }
        host.Add(kind, message, null, null, mergeKey, lasting: true);
    }

    /// <summary>Fades out the toast with this merge key, if one is on screen.</summary>
    public static void Remove(string mergeKey)
    {
        if (Current is not { } host || host.Find(mergeKey) is not { } toast) return;
        toast.Tag = null;
        Animate(toast, 1, 0, () => host.Take(toast), "toast_out");
    }

    private void Take(UIElement toast)
    {
        Children.Remove(toast);
        _lasting.Remove(toast);
    }

    private Border? Find(string mergeKey)
        => Children.OfType<Border>().FirstOrDefault(c => c.Tag is string key && key == mergeKey);

    public void Add(ToastKind kind, string message, string? action, Action? onAction, string? mergeKey = null, bool lasting = false)
    {
        var toast = Build(kind, message, action, onAction, lasting, out var timer);
        toast.Tag = mergeKey;
        var merged = mergeKey is null ? -1 : Children.ToList().FindIndex(c => c is FrameworkElement { Tag: string key } && key == mergeKey);
        if (lasting) _lasting.Add(toast);
        if (merged >= 0)
        {
            Take(Children[merged]);
            Children.Insert(merged, toast);
        }
        else
        {
            while (Children.Count >= MaxToasts) Take(Children.FirstOrDefault(c => !_lasting.Contains(c)) ?? Children[0]);
            Children.Add(toast);
            Animate(toast, 0, 1, null, "toast_in");
        }
        timer?.Start();
    }

    private Border Build(ToastKind kind, string message, string? action, Action? onAction, bool lasting, out DispatcherTimer? timer)
    {
        Border toast = null!;
        void Dismiss()
        {
            toast.Tag = null;
            Animate(toast, 1, 0, () => Take(toast), "toast_out");
        }

        var (glyph, color) = Mark(kind);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        var icon = Ui.Icon(glyph, 18);
        icon.Foreground = new SolidColorBrush(color);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = message, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560, TextTrimming = TextTrimming.CharacterEllipsis });
        if (action is not null)
        {
            var button = new Button
            {
                Style = Ui.Resource<Style>("LwSubtleButton"),
                Content = action,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                Background = Ui.Resource<Brush>("LwButtonBrush"),
                Foreground = Ui.Resource<Brush>("LwAccentFgBrush"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 13,
            };
            AutomationProperties.SetAutomationId(button, "ToastAction");
            button.Click += (_, _) => { Dismiss(); onAction?.Invoke(); };
            row.Children.Add(button);
        }
        var close = new Button
        {
            Style = Ui.Resource<Style>("LwSubtleButton"),
            Width = 28,
            Height = 28,
            Foreground = Ui.Resource<Brush>("LwText3Brush"),
            Content = Ui.Icon(Theme.Icons.Close, 14),
        };
        AutomationProperties.SetName(close, L.Text("Toast.Dismiss"));
        AutomationProperties.SetAutomationId(close, "ToastDismiss");
        close.Click += (_, _) =>
        {
            var key = toast.Tag as string;
            Dismiss();
            if (key is not null) Dismissed?.Invoke(key);
        };
        row.Children.Add(close);

        toast = new Border
        {
            MinHeight = 44,
            Padding = new Thickness(14, 0, 6, 0),
            CornerRadius = new CornerRadius(8),
            Background = new AcrylicBrush
            {
                TintColor = Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C),
                TintOpacity = 0.85,
                FallbackColor = Color.FromArgb(0xF7, 0x2C, 0x2C, 0x2C),
            },
            BorderBrush = Ui.Resource<Brush>("LwPopupBorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = row,
            Translation = new System.Numerics.Vector3(0, 0, 24),
            Shadow = new ThemeShadow(),
        };
        AutomationProperties.SetName(toast, message);
        AutomationProperties.SetAutomationId(toast, "Toast" + kind);
        toast.GettingFocus += RefuseFallingFocus;

        timer = null;
        if (kind != ToastKind.Error && !lasting)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(action is null ? 4 : 6) };
            t.Tick += (_, _) => { t.Stop(); Dismiss(); };
            toast.PointerEntered += (_, _) => t.Stop();
            toast.PointerExited += (_, _) => { if (toast.Parent is not null) t.Start(); };
            timer = t;
        }
        return toast;
    }

    /// <summary>Focus that falls out of a page, because the focused control left the tree, does not
    /// land on a toast's buttons; a click, a tap or Tab from a control on screen still reaches them.</summary>
    private static void RefuseFallingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (args.InputDevice is FocusInputDeviceKind.Mouse or FocusInputDeviceKind.Touch or FocusInputDeviceKind.Pen) return;
        var fell = args.OldFocusedElement is DependencyObject old ? !IsOnScreen(old, sender.XamlRoot) : args.InputDevice == FocusInputDeviceKind.None;
        if (fell) args.TryCancel();
    }

    /// <summary>Whether the element is still in the window's tree or in one of its open popups.</summary>
    private static bool IsOnScreen(DependencyObject element, XamlRoot? root)
    {
        if (root is null) return false;
        var top = element;
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, root.Content)) return true;
            top = node;
        }
        return VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => ReferenceEquals(p, top) || ReferenceEquals(p.Child, top));
    }

    private static void Animate(UIElement target, double from, double to, Action? done, string name)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = Fade };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
        var board = new Storyboard();
        board.Children.Add(animation);
        var frames = Services.FrameLog.Start(name);
        if (done is not null || frames is not null) board.Completed += (_, _) => { frames?.Dispose(); done?.Invoke(); };
        board.Begin();
    }
}
