using System.Windows;

// Fixtures own their windows and must not run App's command-line/startup-window path.
internal static class UiFixtureApplication
{
    public static Application Create()
    {
#pragma warning disable WPF0001 // The production app also opts into the WPF Fluent theme.
        var app = new Application { ThemeMode = ThemeMode.Dark, ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning restore WPF0001
        // Keep the production App.xaml dictionary order so StaticResources resolve identically.
        string[] dictionaries =
        ["Theme/Colors.xaml", "Theme/Effects.xaml", "Theme/Metrics.xaml", "Theme/Typography.xaml",
         "Theme/Icons.xaml", "Theme/Controls.xaml", "Views/MediaTemplates.xaml"];
        foreach (var dictionary in dictionaries)
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/LightWeaver;component/{dictionary}", UriKind.Absolute),
            });
        return app;
    }
}
