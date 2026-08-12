using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Bakabot.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Bakabot.Views.Pages;

public partial class AppearancePage : Page
{
    private readonly AppearanceViewModel _viewModel;

    public AppearancePage(AppearanceViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // 图库为空时显示提示
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceViewModel.GalleryImages))
                UpdateGalleryHint();
        };
        UpdateGalleryHint();
    }

    private void UpdateGalleryHint()
    {
        GalleryEmptyHint.Visibility = _viewModel.GalleryImages.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static bool HasImageFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files != null && files.Any(IsImageFile);
    }

    private static bool IsImageFile(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif";
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasImageFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var imageFile = files?.FirstOrDefault(IsImageFile);
        if (imageFile == null) return;

        try
        {
            _viewModel.ImportAndApplyImage(imageFile);
            UpdateGalleryHint();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入背景失败: {ex.Message}");
        }
    }

    private void DropZone_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _viewModel.SelectImageFileCommand.Execute(null);
        UpdateGalleryHint();
    }
}
