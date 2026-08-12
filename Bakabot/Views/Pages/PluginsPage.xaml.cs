using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Bakabot.ViewModels;

namespace Bakabot.Views.Pages;

public partial class PluginsPage : Page
{
    private readonly PluginsViewModel _viewModel;

    public PluginsPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<PluginsViewModel>();
        DataContext = _viewModel;
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _viewModel.LoadInstanceNamesCommand.Execute(null);
    }
}