using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LightWeaver.WinUI.Shell;

/// <summary>Where a play was started: the card's artwork, which the player grows out of, and the
/// picture it shows, which stands in until the first video frame.</summary>
public sealed record PlayOrigin(FrameworkElement Art, ImageSource? Picture);
