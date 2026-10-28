using LightWeaver.Library;
using LightWeaver.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Controls;

/// <summary>The library's A–Z rail (design 02, 2a note 5): All, #, A–Z top to bottom. Click or drag
/// to jump; a bubble shows the letter under the pointer at once. Letters without items are dimmed.
/// Below 700 px of window height every other letter becomes a dot that still resolves to its
/// letter. Each letter is also a button, for the keyboard and UI Automation.</summary>
internal sealed partial class AlphaRail : Grid
{
    private const string All = "";
    private readonly StackPanel _letters = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _bubble;
    private readonly TextBlock _bubbleText = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly List<(string Letter, Button Button, TextBlock Text)> _entries = [];
    private HashSet<string> _present = [];
    private string? _active;
    private bool _dragging;
    private bool _compressed;

    public AlphaRail()
    {
        Width = 22;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var letter in new[] { All }.Concat(LibraryQuery.RailLetters))
        {
            var text = new TextBlock { FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            var button = new Button { Style = Ui.Resource<Style>("LwSubtleButton"), Width = 22, Height = 17, Padding = new Thickness(0), Content = text, IsHitTestVisible = false };
            AutomationProperties.SetAutomationId(button, "Rail" + (letter == All ? "All" : letter == "#" ? "Hash" : letter));
            var captured = letter;
            button.Click += (_, _) => Jump?.Invoke(this, captured);
            _entries.Add((letter, button, text));
            _letters.Children.Add(button);
        }
        Children.Add(_letters);
        _bubble = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = Ui.Resource<Brush>("LwPaneBrush"),
            BorderBrush = Ui.Resource<Brush>("LwPopupBorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 30, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = _bubbleText,
        };
        Children.Add(_bubble);
        PointerEntered += (_, e) => ShowBubble(e);
        PointerMoved += (_, e) => ShowBubble(e);
        PointerExited += (_, _) => { if (!_dragging) _bubble.Visibility = Visibility.Collapsed; };
        PointerPressed += OnPressed;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => { _dragging = false; _bubble.Visibility = Visibility.Collapsed; };
        ApplyTexts();
        Update();
    }

    /// <summary>A letter was chosen; the empty string means All (the top).</summary>
    public event EventHandler<string>? Jump;

    /// <summary>Letters that have items; the rest are dimmed.</summary>
    public void SetPresent(IEnumerable<string> letters)
    {
        _present = [.. letters];
        Update();
    }

    /// <summary>The letter of the topmost visible item, highlighted in the rail.</summary>
    public void SetActive(string? letter)
    {
        if (_active == letter) return;
        _active = letter;
        Update();
    }

    public bool Compressed
    {
        get => _compressed;
        set { if (_compressed != value) { _compressed = value; Update(); } }
    }

    public void ApplyTexts()
    {
        foreach (var (letter, button, _) in _entries)
            AutomationProperties.SetName(button, letter == All ? L.Text("Library.RailAll") : letter);
    }

    private void Update()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var (letter, button, text) = _entries[i];
            var dot = _compressed && i > 1 && i % 2 == 1;
            text.Text = letter == All ? L.Text("Library.RailAll") : dot ? "•" : letter;
            var present = letter == All || _present.Contains(letter);
            text.Foreground = Ui.Resource<Brush>(letter == _active ? "LwAccentFgBrush" : present ? "LwText2Brush" : "LwText3Brush");
            text.Opacity = present ? 1 : 0.45;
            button.IsEnabled = IsHitTestVisibleRail;
            button.Height = _compressed ? 13 : 17;
        }
    }

    /// <summary>False while the grid is empty: the rail dims and takes no input (design 02, 2f).</summary>
    public bool IsHitTestVisibleRail
    {
        get => IsHitTestVisible;
        set { IsHitTestVisible = value; Opacity = value ? 1 : 0.4; Update(); }
    }

    private string? LetterAt(double y)
    {
        var origin = _letters.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        var height = _letters.ActualHeight / _entries.Count;
        if (height <= 0) return null;
        var index = (int)Math.Floor((y - origin) / height);
        return index >= 0 && index < _entries.Count ? _entries[index].Letter : null;
    }

    private void ShowBubble(PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(this).Position.Y;
        if (LetterAt(y) is not { } letter) { _bubble.Visibility = Visibility.Collapsed; return; }
        _bubbleText.Text = letter == All ? L.Text("Library.RailAll") : letter;
        _bubble.Margin = new Thickness(0, Math.Clamp(y - 20, 0, Math.Max(0, ActualHeight - 40)), 30, 0);
        _bubble.Visibility = Visibility.Visible;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = CapturePointer(e.Pointer);
        ShowBubble(e);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var letter = LetterAt(e.GetCurrentPoint(this).Position.Y);
        ReleasePointerCapture(e.Pointer);
        _dragging = false;
        _bubble.Visibility = Visibility.Collapsed;
        if (letter is not null) Jump?.Invoke(this, letter);
        e.Handled = true;
    }
}
