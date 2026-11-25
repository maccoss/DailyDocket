using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Todos;

namespace DailyDocket.App.Views;

/// <summary>
/// The To do tab. A task is dragged by its grip (or anywhere on the card that is not a control); a
/// line shows where it will land, measured from the cards' middles so the gaps between cards and the
/// space past the last one are places to drop too. The view model turns a drop into one rank change
/// (one file in git).
/// </summary>
public partial class TodoTab : UserControl
{
    // How close to the list's top or bottom edge, in pixels, a drag scrolls it.
    private const double ScrollZone = 36;

    private Point _pressedAt;
    private TodoView? _pressed;
    private InsertionLine? _line;

    public TodoTab() => InitializeComponent();

    private TodoViewModel? Vm => DataContext as TodoViewModel;

    private void OnPressTask(object sender, MouseButtonEventArgs e)
    {
        // Pressing a button, the checkbox or a text box is a click or an edit, never a drag.
        _pressed = IsControl(e.OriginalSource as DependencyObject) ? null : TaskAt(e.OriginalSource);
        _pressedAt = e.GetPosition(OpenList);
    }

    private void OnDragTask(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || _pressed.IsEditing)
        {
            return;
        }

        var moved = e.GetPosition(OpenList) - _pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var dragged = _pressed;
        _pressed = null;

        // The card stays in place but fades while it is carried, so the line is what reads as "here".
        var card = OpenList.ItemContainerGenerator.ContainerFromItem(dragged) as UIElement;
        card?.SetCurrentValue(OpacityProperty, 0.4);
        try
        {
            DragDrop.DoDragDrop(OpenList, new DataObject(typeof(TodoView), dragged), DragDropEffects.Move);
        }
        finally
        {
            card?.ClearValue(OpacityProperty);
            HideLine();
        }
    }

    private void OnDragOverTask(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;
        if (Vm is not { } vm || e.Data.GetData(typeof(TodoView)) is not TodoView dragged)
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        AutoScroll(e.GetPosition(Scroller).Y);

        var (slot, y) = Slot(vm, e.GetPosition(OpenList).Y);
        if (slot >= 0 && TodoRules.TryDropBefore(Ids(vm), dragged.Id, slot, out _))
        {
            ShowLine(y);
        }
        else
        {
            HideLine(); // dropping here would leave it where it is
        }
    }

    private void OnDragLeaveTask(object sender, DragEventArgs e)
    {
        // Leave fires when the pointer crosses into a child too; only hide once it is really outside.
        var p = e.GetPosition(OpenList);
        if (p.X < 0 || p.Y < 0 || p.X > OpenList.ActualWidth || p.Y > OpenList.ActualHeight)
        {
            HideLine();
        }
    }

    private void OnDropTask(object sender, DragEventArgs e)
    {
        HideLine();
        if (Vm is not { } vm || e.Data.GetData(typeof(TodoView)) is not TodoView dragged)
        {
            return;
        }

        var (slot, _) = Slot(vm, e.GetPosition(OpenList).Y);
        if (slot >= 0 && TodoRules.TryDropBefore(Ids(vm), dragged.Id, slot, out var beforeId))
        {
            vm.Drop(dragged, vm.Open.FirstOrDefault(v => v.Id == beforeId));
        }

        e.Handled = true;
    }

    private static List<string> Ids(TodoViewModel vm) => [.. vm.Open.Select(v => v.Id)];

    /// <summary>The slot under <paramref name="y"/> and where its line goes, in the list's coordinates; -1 when it cannot tell.</summary>
    private (int Slot, double LineY) Slot(TodoViewModel vm, double y)
    {
        var boxes = new List<(double Top, double Bottom)>(vm.Open.Count);
        foreach (var view in vm.Open)
        {
            if (OpenList.ItemContainerGenerator.ContainerFromItem(view) is FrameworkElement c && c.IsVisible)
            {
                var top = c.TranslatePoint(new Point(0, 0), OpenList).Y;
                boxes.Add((top, top + c.ActualHeight));
            }
        }

        if (boxes.Count != vm.Open.Count)
        {
            return (-1, 0); // not laid out yet: no move, rather than a guess that sends the task to the bottom
        }

        var slot = TodoRules.DropSlot([.. boxes.Select(b => (b.Top + b.Bottom) / 2)], y);

        // Midway through the gap above the card at the slot (each card's margin is below it).
        var lineY = slot == 0 ? boxes[0].Top + 1
            : slot == boxes.Count ? boxes[^1].Bottom - 3
            : (boxes[slot - 1].Bottom + boxes[slot].Top) / 2 - 3;
        return (slot, lineY);
    }

    private void AutoScroll(double y)
    {
        if (y < ScrollZone)
        {
            Scroller.LineUp();
        }
        else if (y > Scroller.ActualHeight - ScrollZone)
        {
            Scroller.LineDown();
        }
    }

    private void ShowLine(double y)
    {
        if (_line is null && AdornerLayer.GetAdornerLayer(OpenList) is { } layer)
        {
            _line = new InsertionLine(OpenList, (Brush)FindResource("AccentBrush"));
            layer.Add(_line);
        }

        _line?.MoveTo(y);
    }

    private void HideLine()
    {
        if (_line is not null)
        {
            AdornerLayer.GetAdornerLayer(OpenList)?.Remove(_line);
            _line = null;
        }
    }

    private static TodoView? TaskAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = UpTree(d))
        {
            if (d is FrameworkElement { DataContext: TodoView view })
            {
                return view;
            }
        }

        return null;
    }

    private static bool IsControl(DependencyObject? source)
    {
        for (var d = source; d is not null; d = UpTree(d))
        {
            if (d is ButtonBase or TextBoxBase or DatePicker)
            {
                return true;
            }

            if (d is ItemsControl)
            {
                return false;
            }
        }

        return false;
    }

    private static DependencyObject? UpTree(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    /// <summary>The accent line drawn across the list where a dragged task would land.</summary>
    private sealed class InsertionLine : Adorner
    {
        private readonly Pen _pen;
        private double _y;

        public InsertionLine(UIElement list, Brush brush)
            : base(list)
        {
            IsHitTestVisible = false; // the drop must reach the list beneath
            _pen = new Pen(brush, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            _pen.Freeze();
        }

        public void MoveTo(double y)
        {
            if (Math.Abs(y - _y) > 0.5)
            {
                _y = y;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var width = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawLine(_pen, new Point(2, _y), new Point(width - 2, _y));
            dc.DrawEllipse(_pen.Brush, null, new Point(4, _y), 4, 4);
        }
    }
}
