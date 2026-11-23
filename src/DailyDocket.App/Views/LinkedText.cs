using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DailyDocket.Core.Text;

namespace DailyDocket.App.Views;

/// <summary>
/// LinkedText.Text on a TextBlock: shows the text with its web addresses as links that open in the
/// browser (a to-do's notes, where a Gmail address leads back to the email).
/// </summary>
public static class LinkedText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LinkedText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);

    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Inlines.Clear();
        foreach (var piece in Links.Split(e.NewValue as string))
        {
            if (piece.Link is null)
            {
                block.Inlines.Add(new Run(piece.Text));
                continue;
            }

            var link = new Hyperlink(new Run(piece.Text)) { NavigateUri = piece.Link, ToolTip = piece.Link.AbsoluteUri };
            link.RequestNavigate += (_, args) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Serilog.Log.Warning(ex, "Opening a link from a to-do failed");
                }

                args.Handled = true;
            };
            block.Inlines.Add(link);
        }
    }
}
