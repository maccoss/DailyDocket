using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DailyDocket.App.ViewModels;

namespace DailyDocket.App.Views;

/// <summary>
/// The To do tab. Dragging a task onto another drops it above or below that task, by which half of
/// the card it lands on; the view model turns that into one rank change (one file in git).
/// </summary>
public partial class TodoTab : UserControl
{
    private Point _pressedAt;
    private TodoView? _pressed;

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
        DragDrop.DoDragDrop(OpenList, new DataObject(typeof(TodoView), dragged), DragDropEffects.Move);
    }

    private void OnDragOverTask(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(TodoView)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropTask(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm || e.Data.GetData(typeof(TodoView)) is not TodoView dragged)
        {
            return;
        }

        var target = TaskAt(e.OriginalSource);
        if (target is null || ReferenceEquals(target, dragged))
        {
            return;
        }

        // Below the middle of the card: drop after it, which is before the task that follows it.
        var container = OpenList.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement;
        var below = container is not null && e.GetPosition(container).Y > container.ActualHeight / 2;
        var order = vm.Open.ToList();
        TodoView? before = target;
        if (below)
        {
            var next = order.IndexOf(target) + 1;
            before = next < order.Count ? order[next] : null;
            if (ReferenceEquals(before, dragged))
            {
                return; // already there
            }
        }

        vm.Drop(dragged, before);
        e.Handled = true;
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
}
