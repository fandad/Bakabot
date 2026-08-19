using System.Windows;
using System.Windows.Controls;
using Bakabot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bakabot.Views.Pages;

public partial class CommandPromptPage : Page
{
    private readonly CommandPromptViewModel _viewModel;

    public CommandPromptPage()
    {
        InitializeComponent();
        _viewModel = App.Services.GetRequiredService<CommandPromptViewModel>();
        DataContext = _viewModel;
    }

    private void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is QuickCommandRow row)
            _viewModel.RemoveRowCommand.Execute(row);
    }
}
