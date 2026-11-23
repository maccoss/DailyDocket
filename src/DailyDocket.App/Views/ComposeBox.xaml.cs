using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using DailyDocket.App.Services;
using DailyDocket.App.ViewModels;

namespace DailyDocket.App.Views;

/// <summary>
/// The reply box. The view owns the rich text (WPF's document has no clean place in a view model);
/// it hands the view model the text in every form a send needs, on each typing pause and on Send.
/// </summary>
public partial class ComposeBox : UserControl
{
    private readonly DispatcherTimer _pause = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private ComposeViewModel? _vm;
    private bool _loading;

    public ComposeBox()
    {
        InitializeComponent();
        _pause.Tick += (_, _) =>
        {
            _pause.Stop();
            _vm?.SaveDraft(DocumentConverter.Convert(Editor.Document));
        };
        DataContextChanged += (_, e) =>
        {
            if (_vm is not null)
            {
                _vm.LoadRequested -= OnLoadRequested;
            }

            _vm = e.NewValue as ComposeViewModel;
            if (_vm is not null)
            {
                _vm.LoadRequested += OnLoadRequested;
            }
        };
    }

    private void OnLoadRequested(string? xaml)
    {
        _pause.Stop();
        _loading = true;
        var doc = DocumentConverter.Load(xaml);
        doc.FontFamily = Editor.FontFamily;
        doc.FontSize = Editor.FontSize;
        doc.PagePadding = new Thickness(2);
        Editor.Document = doc;
        _loading = false;
        Dispatcher.BeginInvoke(() =>
        {
            Editor.Focus();
            Editor.CaretPosition = Editor.Document.ContentEnd;
        }, DispatcherPriority.Input);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _pause.Stop();
        _pause.Start();
    }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        _pause.Stop();
        _vm?.Send(DocumentConverter.Convert(Editor.Document));
    }

    /// <summary>Makes the selected text a link to an address the user types.</summary>
    private void OnLink(object sender, RoutedEventArgs e)
    {
        var selection = Editor.Selection;
        if (selection.IsEmpty)
        {
            MessageBox.Show(Window.GetWindow(this), "Select the text to link first.", "Link", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var url = InputDialog.Ask(Window.GetWindow(this), "Link", "Address (https://...)", "https://");
        if (url is null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
        {
            return;
        }

        // The constructor wraps the selection in the document itself.
        _ = new Hyperlink(selection.Start, selection.End) { NavigateUri = uri };
        Editor.Focus();
    }
}

/// <summary>A one-line question in a small window.</summary>
internal static class InputDialog
{
    public static string? Ask(Window? owner, string title, string prompt, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 360, Margin = new Thickness(0, 6, 0, 10) };
        var ok = new Button { Content = "OK", IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Children =
                {
                    new TextBlock { Text = prompt },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
                },
            },
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        return window.ShowDialog() == true ? box.Text : null;
    }
}
