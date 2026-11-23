using System.ComponentModel;
using System.Windows;
using DailyDocket.App.ViewModels;
using DailyDocket.Core.Infrastructure;

namespace DailyDocket.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;

    public MainWindow(MainViewModel viewModel, SettingsStore settingsStore, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsStore = settingsStore;
        _settings = settings;
        DataContext = viewModel;

        if (settings.WindowWidth is > 400 and var width && settings.WindowHeight is > 300 and var height)
        {
            Width = width;
            Height = height;
        }

        Loaded += (_, _) => _viewModel.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settingsStore.Save(_settings);
        }

        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
