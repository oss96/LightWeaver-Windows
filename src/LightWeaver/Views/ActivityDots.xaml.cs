using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LightWeaver.Views;

/// <summary>
/// The app-wide activity indicator: three dots throbbing in sequence (2026-08-03).
///
/// Replaces <c>LwActivityRingSmall</c> / <c>LwBufferingIndicator</c>, whose breathing
/// circle was borrowed from the player and did not survive being shrunk - see the class
/// comment in ActivityDots.xaml.
///
/// Used at two sizes: the browse default (5 px dots) in the load-more pill and the search
/// pill, and a larger band (9 px) in the player overlay, where <see cref="PeakOpacity"/> is
/// held at 0.55 to stay inside the glow budget that keeps the overlay from blooming over
/// HDR video.
/// </summary>
public partial class ActivityDots : UserControl
{
    // Timing lives here rather than as a Metrics.xaml Duration token because the storyboard
    // is assembled in code (see the XAML comment) - a token would be a second source of
    // truth for a single consumer. Metrics.xaml's motion block points at this file instead.
    private const int DotCount = 3;
    private const int CycleMs = 1050;    // one dot: dim -> lit -> dim, then holds dim
    private const int StaggerMs = 140;   // each dot starts this much after its neighbour
    private const double PeakAt = 0.35;  // fraction of the cycle at full brightness
    private const double SettleAt = 0.70; // ...back to dim by here, then it waits
    private const double DimScale = 0.78;
    private const double PeakScale = 1.18;

    private Storyboard? _storyboard;
    private bool _built;

    public static readonly DependencyProperty DotSizeProperty = DependencyProperty.Register(
        nameof(DotSize), typeof(double), typeof(ActivityDots), new PropertyMetadata(5.0));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ActivityDots), new PropertyMetadata(4.0));

    public static readonly DependencyProperty DimOpacityProperty = DependencyProperty.Register(
        nameof(DimOpacity), typeof(double), typeof(ActivityDots), new PropertyMetadata(0.26));

    public static readonly DependencyProperty PeakOpacityProperty = DependencyProperty.Register(
        nameof(PeakOpacity), typeof(double), typeof(ActivityDots), new PropertyMetadata(0.95));

    /// <summary>Diameter of one dot, in DIP.</summary>
    public double DotSize
    {
        get => (double)GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    /// <summary>Space between neighbouring dots, in DIP.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Opacity a dot rests at while its neighbours are lit.</summary>
    public double DimOpacity
    {
        get => (double)GetValue(DimOpacityProperty);
        set => SetValue(DimOpacityProperty, value);
    }

    /// <summary>
    /// Opacity at the top of a dot's throb. Keep this at or below 0.55 anywhere the
    /// indicator sits over video - that is the overlay's glow budget for HDR.
    /// </summary>
    public double PeakOpacity
    {
        get => (double)GetValue(PeakOpacityProperty);
        set => SetValue(PeakOpacityProperty, value);
    }

    public ActivityDots()
    {
        InitializeComponent();
        // Deferred to Loaded so values set in XAML are in place first; the constructor
        // runs before any attribute is applied.
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /// <summary>
    /// Decorative, so it stays out of the UIA tree entirely - exactly as the Ellipse it
    /// replaces did (a Shape has no automation peer). The suites find these indicators by
    /// the AutomationId on the caption beside them, never by the spinner itself, and this
    /// keeps the tree they assert against unchanged.
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => null!;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Build();
        if (IsVisible)
            _storyboard?.Begin(this, true);
    }

    /// <summary>
    /// The pill spends most of its life collapsed, and WPF keeps animating a collapsed
    /// element's properties. Five instances times six timelines is not free, so the
    /// storyboard runs only while the indicator is actually on screen.
    /// </summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_built)
            return;
        if (IsVisible)
            _storyboard?.Begin(this, true);
        else
            _storyboard?.Stop(this);
    }

    private void Build()
    {
        if (_built)
            return;
        _built = true;

        var fill = (Brush)FindResource("LwLightCoreBrush");
        // The children repeat forever individually; repeating the storyboard too would be a
        // second, redundant loop over timelines that never end.
        var storyboard = new Storyboard();

        for (var i = 0; i < DotCount; i++)
        {
            var scale = new ScaleTransform(DimScale, DimScale);
            var dot = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                Fill = fill,
                Opacity = DimOpacity,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
                Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
            };
            Host.Children.Add(dot);

            // Names are needed because Storyboard.SetTargetName is the only way to reach a
            // transform's own properties by path; targeting the element directly cannot
            // address RenderTransform.ScaleX on a per-child basis.
            var dotName = "Dot" + i;
            var scaleName = "DotScale" + i;
            RegisterName(dotName, dot);
            RegisterName(scaleName, scale);

            var begin = TimeSpan.FromMilliseconds(i * StaggerMs);
            storyboard.Children.Add(Fade(dotName, begin));
            storyboard.Children.Add(Grow(scaleName, begin, ScaleTransform.ScaleXProperty));
            storyboard.Children.Add(Grow(scaleName, begin, ScaleTransform.ScaleYProperty));
        }

        _storyboard = storyboard;
    }

    private DoubleAnimationUsingKeyFrames Fade(string targetName, TimeSpan begin)
    {
        var anim = KeyFrames(DimOpacity, PeakOpacity, begin);
        Storyboard.SetTargetName(anim, targetName);
        Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
        return anim;
    }

    private DoubleAnimationUsingKeyFrames Grow(string targetName, TimeSpan begin, DependencyProperty property)
    {
        var anim = KeyFrames(DimScale, PeakScale, begin);
        Storyboard.SetTargetName(anim, targetName);
        Storyboard.SetTargetProperty(anim, new PropertyPath(property));
        return anim;
    }

    /// <summary>
    /// dim -> peak -> dim -> hold. The hold is what makes three dots read as a sequence
    /// rather than three things pulsing at once: each dot is bright for about a third of
    /// the cycle and dark for the rest, so the lit one appears to travel.
    /// </summary>
    private static DoubleAnimationUsingKeyFrames KeyFrames(double dim, double peak, TimeSpan begin)
    {
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var anim = new DoubleAnimationUsingKeyFrames
        {
            BeginTime = begin,
            Duration = new Duration(TimeSpan.FromMilliseconds(CycleMs)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(dim, KeyTime.FromTimeSpan(TimeSpan.Zero), ease));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(CycleMs * PeakAt)), ease));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            dim, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(CycleMs * SettleAt)), ease));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            dim, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(CycleMs)), ease));
        return anim;
    }
}
