using LightWeaver.Home;
using LightWeaver.Library;
using LightWeaver.Localization;
using LightWeaver.WinUI.Shell;

namespace LightWeaver.WinUI.Controls;

/// <summary>One Home row (Home sketch): "Title →" opens the section. Continue Watching and Next Up
/// use 16:9 cards, the others posters.</summary>
internal sealed partial class HomeRow : CardRow
{
    public HomeRow(IAppShell shell, HomeSection section, UserDataOverrides overrides, ItemMenuOptions? menu = null)
        : base(shell, "HomeRow", section.ToString(), () => TitleOf(section), () => shell.OpenSection(section),
            HomeSections.IsWide(section) ? CardKind.Wide : CardKind.Poster,
            Ui.Resource<double>(HomeSections.IsWide(section) ? "LwHomeWideCardWidth" : "LwHomePosterWidth"), overrides, menu)
    {
        Section = section;
        CardTitle = HomeSections.Title;
        CardSubtitle = i => HomeSections.Subtitle(section, i);
    }

    public HomeSection Section { get; }

    private static string TitleOf(HomeSection section) => section switch
    {
        HomeSection.ContinueWatching => L.Text("Home.ContinueWatching"),
        HomeSection.NextUp => L.Text("Home.NextUp"),
        HomeSection.RecentlyAdded => L.Text("Home.RecentlyAdded"),
        _ => L.Text("Home.Favorites"),
    };
}
