using System.Windows;
using DailyDocket.App.ViewModels;

namespace DailyDocket.App.Views;

public partial class SetupWindow : Window
{
    private SetupWindow(SetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    /// <summary>Shows the checklist over <paramref name="owner"/> and waits until it is closed.</summary>
    public static void Show(Window? owner, SetupViewModel viewModel)
    {
        var window = new SetupWindow(viewModel);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
