using LightWeaver.Localization;
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
    static CardCommands()
    {
        L.Source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(LocalizationSource.Revision)) return;
            ToggleWatched.Text = L.Text("CardCommands.ToggleWatched.638fbe5f");
            ShowMediaInfo.Text = L.Text("CardCommands.MediaInfo.6c137e38");
            CopyStreamUrl.Text = L.Text("CardCommands.CopyStreamUrl.1673f88f");
            OpenCardMenu.Text = L.Text("CardCommands.CardMenu.f6c7dbe6");
            PlayItem.Text = L.Text("CardCommands.PlayItem.4fc83323");
        };
    }

    public static readonly RoutedUICommand ToggleWatched =
        new(L.Text("CardCommands.ToggleWatched.638fbe5f"), nameof(ToggleWatched), typeof(CardCommands));

    public static readonly RoutedUICommand ShowMediaInfo =
        new(L.Text("CardCommands.MediaInfo.6c137e38"), nameof(ShowMediaInfo), typeof(CardCommands));

    public static readonly RoutedUICommand CopyStreamUrl =
        new(L.Text("CardCommands.CopyStreamUrl.1673f88f"), nameof(CopyStreamUrl), typeof(CardCommands));

    /// <summary>Opens the card's overflow menu (the ⋯ button) — the menu itself is built
    /// in MainWindow, mirroring ItemDetailView.OnMore.</summary>
    public static readonly RoutedUICommand OpenCardMenu =
        new(L.Text("CardCommands.CardMenu.f6c7dbe6"), nameof(OpenCardMenu), typeof(CardCommands));

    /// <summary>Plays the card's media item directly from the hover play button.</summary>
    public static readonly RoutedUICommand PlayItem =
        new(L.Text("CardCommands.PlayItem.4fc83323"), nameof(PlayItem), typeof(CardCommands));
}
