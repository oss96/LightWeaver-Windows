using LightWeaver.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LightWeaver.WinUI.Controls;

/// <summary>A suggestion in a <see cref="TokenBox"/>: what is shown and what is chosen.</summary>
public sealed record TokenSuggestion(string Label, object Value)
{
    /// <summary>What list items and UI Automation show.</summary>
    public override string ToString() => Label;
}

public sealed partial class TokenBox : UserControl
{
    private readonly List<TokenSuggestion> _tokens = [];
    private CancellationTokenSource? _suggest;

    public TokenBox()
    {
        InitializeComponent();
        FastUi.Flyout(SuggestFlyout);
    }

    /// <summary>Returns suggestions for the typed text; the box drops the ones already chosen.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TokenSuggestion>>>? Suggest { get; set; }

    public string PlaceholderText { get => Placeholder.Text; set => Placeholder.Text = value; }

    /// <summary>The input's automation id, so UI tests can type into it.</summary>
    public string InputAutomationId
    {
        set
        {
            AutomationProperties.SetAutomationId(Input, value);
            AutomationProperties.SetAutomationId(Suggestions, value + "Suggestions");
        }
    }

    /// <summary>The input's accessible name.</summary>
    public string InputName { set => AutomationProperties.SetName(Input, value); }

    public IReadOnlyList<TokenSuggestion> Values => _tokens;

    /// <summary>Raised when a token is added or removed; <see cref="Values"/> holds the new set.</summary>
    public event EventHandler? ValuesChanged;

    /// <summary>Replaces the tokens without raising <see cref="ValuesChanged"/> (the filters changed elsewhere).</summary>
    public void SetValues(IEnumerable<TokenSuggestion> values)
    {
        _tokens.Clear();
        _tokens.AddRange(values);
        Rebuild();
    }

    private void Rebuild()
    {
        while (Tokens.Children.Count > 1) Tokens.Children.RemoveAt(0);
        for (var i = 0; i < _tokens.Count; i++) Tokens.Children.Insert(i, MakeToken(_tokens[i]));
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Design: 22 px high, padding 0 3 0 8, radius 4, accentSoft fill, 12 px text, a 13 px close glyph.
    private FrameworkElement MakeToken(TokenSuggestion token)
    {
        var remove = Ui.TokenRemoveButton(3);
        AutomationProperties.SetName(remove, L.Format("Controls.RemoveToken", token.Label));
        AutomationProperties.SetAutomationId(remove, "RemoveToken");
        remove.Click += (_, _) =>
        {
            _tokens.Remove(token);
            Rebuild();
            ValuesChanged?.Invoke(this, EventArgs.Empty);
            Input.Focus(FocusState.Programmatic);
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = token.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(remove);
        return new Border
        {
            Height = 22,
            Padding = new Thickness(8, 0, 2, 0),
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["LwAccentSoftBrush"],
            Child = panel,
        };
    }

    private async void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suggest?.Cancel();
        var term = Input.Text.Trim();
        if (term.Length == 0 || Suggest is null)
        {
            SuggestFlyout.Hide();
            return;
        }
        var cts = _suggest = new CancellationTokenSource();
        IReadOnlyList<TokenSuggestion> found;
        try { found = await Suggest(term, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { found = []; }
        if (cts.IsCancellationRequested) return;
        var fresh = found.Where(s => !_tokens.Any(t => Equals(t.Value, s.Value))).ToList();
        Suggestions.ItemsSource = fresh;
        if (fresh.Count == 0) SuggestFlyout.Hide();
        else if (!SuggestFlyout.IsOpen)
            SuggestFlyout.ShowAt(this, new FlyoutShowOptions { ShowMode = FlyoutShowMode.Transient, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when Suggestions.ItemsSource is IReadOnlyList<TokenSuggestion> { Count: > 0 } list && SuggestFlyout.IsOpen:
                Add(list[0]);
                e.Handled = true;
                break;
            case VirtualKey.Down when SuggestFlyout.IsOpen:
                Suggestions.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
            case VirtualKey.Back when Input.Text.Length == 0 && _tokens.Count > 0:
                _tokens.RemoveAt(_tokens.Count - 1);
                Rebuild();
                ValuesChanged?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case VirtualKey.Escape when SuggestFlyout.IsOpen:
                SuggestFlyout.Hide();
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            SuggestFlyout.Hide();
            Input.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    private void OnSuggestionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TokenSuggestion s) Add(s);
    }

    private void Add(TokenSuggestion s)
    {
        SuggestFlyout.Hide();
        if (!_tokens.Any(t => Equals(t.Value, s.Value))) _tokens.Add(s);
        Input.Text = "";
        Rebuild();
        ValuesChanged?.Invoke(this, EventArgs.Empty);
        Input.Focus(FocusState.Programmatic);
    }

    // Focused: 2 px accent bottom line, like the search field.
    private void OnFocusChanged(object sender, RoutedEventArgs e)
    {
        var focused = Input.FocusState != FocusState.Unfocused;
        Bottom.Background = (Brush)Application.Current.Resources[focused ? "LwAccentFgBrush" : "LwFieldBottomBrush"];
        Bottom.Height = focused ? 2 : 1;
    }
}
