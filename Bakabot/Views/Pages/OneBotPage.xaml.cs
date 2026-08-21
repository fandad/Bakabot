using System.Windows;
using System.Windows.Controls;
using Bakabot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bakabot.Views.Pages;

public partial class OneBotPage : Page
{
    private readonly OneBotViewModel _viewModel;

    public OneBotPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<OneBotViewModel>();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OneBotViewModel.IsRunning)
                or nameof(OneBotViewModel.ClientCount))
                UpdateButtonStates();
        };
        UpdateButtonStates();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshStatus();
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        StartBtn.IsEnabled = !_viewModel.IsRunning;
        StopBtn.IsEnabled = _viewModel.IsRunning;
    }
}
