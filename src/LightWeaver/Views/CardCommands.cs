using System.Windows.Input;

namespace LightWeaver.Views;

/// <summary>
/// Routed commands raised by the shared media-card templates' hover action bar (Phase 7
/// M10). Cards are DataTemplates with no code-behind, so the buttons bind these commands
/// with the card's MediaItem as parameter; MainWindow registers the CommandBindings once
/// (it already owns AppViewModel and the ItemUserDataChanged fan-out).
/// </summary>
public static class CardCommands
{
    public static readonly RoutedUICommand ToggleWatched =
        new("Toggle watched", nameof(ToggleWatched), typeof(CardCommands));

    public static readonly RoutedUICommand ShowMediaInfo =
        new("Media info", nameof(ShowMediaInfo), typeof(CardCommands));

    public static readonly RoutedUICommand CopyStreamUrl =
        new("Copy stream URL", nameof(CopyStreamUrl), typeof(CardCommands));

    /// <summary>Opens the card's overflow menu (the ⋯ button) — the menu itself is built
    /// in MainWindow, mirroring ItemDetailView.OnMore.</summary>
    public static readonly RoutedUICommand OpenCardMenu =
        new("Card menu", nameof(OpenCardMenu), typeof(CardCommands));

    /// <summary>Plays the card's media item directly from the hover play button.</summary>
    public static readonly RoutedUICommand PlayItem =
        new("Play item", nameof(PlayItem), typeof(CardCommands));
}
