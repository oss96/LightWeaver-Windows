using LightWeaver.Localization;
using LightWeaver.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace LightWeaver.WinUI.Controls;

internal sealed class HomeSectionDrag(string profileKey, Func<bool> canEdit, Action changed)
{
    private readonly List<(HomeSectionId Id, Border Row, Thickness Thickness, Brush? Brush)> _targets = [];
    private readonly Dictionary<HomeSectionId, Thumb> _handles = [];
    private HomeSectionId? _dragged;
    private Point _position;
    private long _generation;
    private uint? _pointerId;
    private bool _releasePending;

    public Thumb Handle(HomeSectionId id, string name, string prefix)
    {
        var handle = new Thumb { Style = Ui.Resource<Style>("LwHomeSectionGrip") };
        AutomationProperties.SetAutomationId(handle, prefix + id);
        AutomationProperties.SetName(handle, L.Format("Home.ReorderSection", name));
        ToolTipService.SetToolTip(handle, L.Text("Home.ReorderHint"));
        _handles[id] = handle;
        handle.DragStarted += (_, e) =>
        {
            Cancel();
            if (!canEdit() || handle.Parent is not UIElement parent) return;
            _dragged = id;
            _position = parent.TransformToVisual(null).TransformPoint(new Point(e.HorizontalOffset, e.VerticalOffset));
            handle.Focus(FocusState.Programmatic);
        };
        handle.DragDelta += (_, e) =>
        {
            if (_dragged is null) return;
            if (!canEdit()) { Cancel(); return; }
            _position = new Point(_position.X + e.HorizontalChange, _position.Y + e.VerticalChange);
            ClearIndicators();
            if (Target() is not { } target || target.Id == _dragged) return;
            var row = _targets.Single(t => t.Id == target.Id).Row;
            row.BorderBrush = Ui.Resource<Brush>("LwAccentFgBrush");
            row.BorderThickness = target.After ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
        };
        handle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (_dragged != id) return;
            _pointerId = e.Pointer.PointerId;
            _position = e.GetCurrentPoint(null).Position;
        }), true);
        handle.DragCompleted += (_, e) =>
        {
            if (e.Canceled || !canEdit()) { Cancel(); return; }
            _releasePending = true;
            var generation = _generation;
            handle.DispatcherQueue.TryEnqueue(() => { if (generation == _generation) Cancel(); });
        };
        handle.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) =>
        {
            if (!_releasePending || _pointerId != e.Pointer.PointerId) return;
            _position = e.GetCurrentPoint(null).Position;
            var source = _dragged;
            var target = canEdit() ? Target() : null;
            Cancel();
            if (source is { } from && target is { } to) Move(from, to.Id, to.After);
        }), true);
        handle.PointerCanceled += (_, _) => Cancel();
        handle.Unloaded += (_, _) => Cancel();
        handle.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && _dragged is not null)
            {
                handle.CancelDrag();
                Cancel();
                e.Handled = true;
                return;
            }
            if (e.Handled || e.Key is not (VirtualKey.Up or VirtualKey.Down)
                || !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)
                || !canEdit()) return;
            e.Handled = true;
            var rows = HomeLayoutStore.Load(profileKey).Where(p => p.Id != HomeSectionId.Libraries).ToList();
            var at = rows.FindIndex(p => p.Id == id);
            var target = at + (e.Key == VirtualKey.Up ? -1 : 1);
            if (at >= 0 && target >= 0 && target < rows.Count) Move(id, rows[target].Id, target > at);
        };
        return handle;
    }

    public void Attach(Border row, HomeSectionId id)
        => _targets.Add((id, row, row.BorderThickness, row.BorderBrush));

    private (HomeSectionId Id, bool After)? Target()
    {
        var bounds = _targets.Where(t => t.Row.IsLoaded && t.Row.ActualHeight > 0).Select(t =>
        {
            var origin = t.Row.TransformToVisual(null).TransformPoint(new Point());
            return (t.Id, Left: origin.X, Top: origin.Y, Width: t.Row.ActualWidth, Height: t.Row.ActualHeight);
        }).OrderBy(t => t.Top).ToArray();
        if (bounds.Length == 0 || _position.X < bounds.Min(b => b.Left) || _position.X > bounds.Max(b => b.Left + b.Width)
            || _position.Y < bounds[0].Top || _position.Y > bounds[^1].Top + bounds[^1].Height) return null;
        return HomeSectionOrder.DropTarget(bounds.Select(b => (b.Id, b.Top, b.Height)).ToArray(), _position.Y);
    }

    private void Move(HomeSectionId source, HomeSectionId target, bool after)
    {
        if (!canEdit()) return;
        var layout = HomeLayoutStore.Load(profileKey);
        var reordered = HomeSectionOrder.Move(layout, source, target, after);
        if (layout.SequenceEqual(reordered)) return;
        HomeLayoutStore.Save(profileKey, reordered);
        if (_handles.TryGetValue(source, out var handle) && handle.IsLoaded) handle.Focus(FocusState.Programmatic);
        changed();
        if (handle is not null) handle.DispatcherQueue.TryEnqueue(() => { if (handle.IsLoaded) handle.Focus(FocusState.Programmatic); });
    }

    private void Cancel()
    {
        _generation++;
        _dragged = null;
        _pointerId = null;
        _releasePending = false;
        ClearIndicators();
    }

    private void ClearIndicators()
    {
        foreach (var target in _targets)
        {
            target.Row.BorderThickness = target.Thickness;
            target.Row.BorderBrush = target.Brush;
        }
    }
}
