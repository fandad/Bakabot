using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Bakabot.Models;
using Bakabot.Services;
using Bakabot.ViewModels;
using Bakabot.Views.Dialogs;

namespace Bakabot.Views.Pages;

public partial class HomePage : Page
{
    private readonly HomeViewModel _viewModel;
    private readonly InstanceManager _instanceManager;

    public HomePage()
    {
        InitializeComponent();

        _viewModel = App.Services.GetRequiredService<HomeViewModel>();
        _instanceManager = App.Services.GetRequiredService<InstanceManager>();
        DataContext = _viewModel;

        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HomeViewModel.StatusMessage))
            {
                StatusLabel.Text = _viewModel.StatusMessage;
            }
        };

        _viewModel.RequestNavigateToConsole += (instanceName) =>
        {
            if (Window.GetWindow(this) is MainWindow mainWindow)
            {
                mainWindow.NavigateToConsole(instanceName);
            }
        };
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadInstancesCommand.Execute(null);
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyStatePanel.Visibility = _viewModel.Instances.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void StartInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BotInstance instance })
        {
            await _viewModel.StartInstanceCommand.ExecuteAsync(instance);
        }
    }

    private void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BotInstance instance })
        {
            ShowCreateDialog(instance);
        }
    }

    private void DuplicateInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BotInstance instance })
        {
            _viewModel.DuplicateInstanceCommand.Execute(instance);
        }
    }

    private async void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BotInstance instance })
        {
            await _viewModel.DeleteInstanceCommand.ExecuteAsync(instance);
            UpdateEmptyState();
        }
    }

    private void ClearInstanceLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: BotInstance instance })
        {
            _viewModel.ClearInstanceLogCommand.Execute(instance);
        }
    }

    private void CreateInstance_Click(object sender, RoutedEventArgs e)
    {
        ShowCreateDialog(null);
    }

    /// <summary>
    /// 弹出创建/编辑实例对话框。
    /// </summary>
    private void ShowCreateDialog(BotInstance? editInstance)
    {
        var dialog = new CreateInstanceDialog();
        var vm = App.Services.GetRequiredService<CreateInstanceViewModel>();

        if (editInstance != null)
        {
            vm.LoadForEdit(editInstance);
        }
        else
        {
            vm.ResetForCreate();
        }

        dialog.DataContext = vm;

        vm.SaveCompleted += () =>
        {
            dialog.Close();
            _viewModel.LoadInstancesCommand.Execute(null);
            UpdateEmptyState();
        };

        dialog.Owner = Window.GetWindow(this);
        dialog.ShowDialog();
    }
}