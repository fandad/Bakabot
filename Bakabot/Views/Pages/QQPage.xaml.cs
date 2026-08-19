using System.Windows;
using System.Windows.Controls;
using Bakabot.Models;
using Bakabot.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Wpf.Ui.Controls;

namespace Bakabot.Views.Pages;

public partial class QQPage : Page
{
    private readonly QQViewModel _viewModel;

    public QQPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<QQViewModel>();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(QQViewModel.IsDownloadingNapCat))
            {
                NapCatProgressBar.Visibility = _viewModel.IsDownloadingNapCat
                    ? Visibility.Visible : Visibility.Collapsed;
                UpdateButtonStates();
            }
            if (e.PropertyName == nameof(QQViewModel.Rows))
                UpdateEmptyHint();
            if (e.PropertyName is nameof(QQViewModel.IsNapCatRunning)
                or nameof(QQViewModel.IsNapCatDownloaded))
                UpdateButtonStates();
        };
        UpdateButtonStates();
        UpdateEmptyHint();
    }

    private void UpdateButtonStates()
    {
        var vm = _viewModel;
        DownloadNapCatBtn.IsEnabled = !vm.IsDownloadingNapCat && !vm.IsNapCatRunning;
        StartNapCatBtn.IsEnabled = !vm.IsNapCatRunning && vm.IsNapCatDownloaded;
        StopNapCatBtn.IsEnabled = vm.IsNapCatRunning;
        OpenWebUIBtn.IsEnabled = vm.IsNapCatRunning;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshStatusCommand.Execute(null);
        NewQQBox.Focus();
    }

    private void UpdateEmptyHint()
    {
        EmptyWhitelistHint.Visibility = _viewModel.Rows.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EntryToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.DataContext is WhitelistRow row)
            _viewModel.SetEntryEnabled(row, true);
    }

    private void EntryToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.DataContext is WhitelistRow row)
            _viewModel.SetEntryEnabled(row, false);
    }

    private void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is WhitelistRow row)
            _viewModel.RemoveWhitelistCommand.Execute(row.Entry.QQ);
    }
}
