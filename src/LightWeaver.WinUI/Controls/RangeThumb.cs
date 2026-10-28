using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace LightWeaver.WinUI.Controls;

/// <summary>One thumb of a <see cref="RangeSlider"/>: the stock Slider's 20 px thumb with a 12 px
/// accent dot, draggable, focusable, and seen by UI Automation as a range value (read and set),
/// so screen readers and the UI suites can drive the year range. WinUI's Thumb is sealed, hence
/// a control of its own.</summary>
public sealed partial class RangeThumb : ContentControl
{
    private double _lastX;
    private bool _dragging;

    public RangeThumb()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        var grid = new Grid { Width = 20, Height = 20, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.Children.Add(new Ellipse
        {
            Fill = Ui.Resource<Brush>("LwKnobBrush"),
            Stroke = Ui.Resource<Brush>("LwKnobBorderBrush"),
            StrokeThickness = 1,
        });
        grid.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = Ui.Resource<Brush>("LwAccentFgBrush") });
        Content = grid;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, _) => _dragging = false;
    }

    internal Func<double>? Read { get; set; }
    internal Action<double>? Write { get; set; }
    internal double Minimum { get; set; }
    internal double Maximum { get; set; }

    /// <summary>Raised while dragging, with the horizontal movement in DIPs since the last event.</summary>
    public event EventHandler<double>? Dragged;
    public event EventHandler? DragStarted;

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Parent is not UIElement parent) return;
        _dragging = CapturePointer(e.Pointer);
        _lastX = e.GetCurrentPoint(parent).Position.X;
        Focus(FocusState.Pointer);
        DragStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Parent is not UIElement parent) return;
        var x = e.GetCurrentPoint(parent).Position.X;
        Dragged?.Invoke(this, x - _lastX);
        _lastX = x;
        e.Handled = true;
    }

    private void EndDrag(Pointer pointer)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(pointer);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Tells assistive tech that the value moved.</summary>
    internal void RaiseValueChanged(double oldValue, double newValue)
    {
        if (FrameworkElementAutomationPeer.FromElement(this) is AutomationPeer peer)
            peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);
    }

    private sealed partial class Peer(RangeThumb owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(RangeThumb);
        protected override object GetPatternCore(PatternInterface patternInterface)
            => patternInterface == PatternInterface.RangeValue ? this : base.GetPatternCore(patternInterface);

        public bool IsReadOnly => false;
        public double LargeChange => 10;
        public double SmallChange => 1;
        public double Maximum => owner.Maximum;
        public double Minimum => owner.Minimum;
        public double Value => owner.Read?.Invoke() ?? 0;
        public void SetValue(double value) => owner.Write?.Invoke(value);
    }
}
