namespace Bakabot.Views.Pages;

using System.Windows.Controls;

public partial class DocsPage : Page
{
    public DocsPage()
    {
        InitializeComponent();
        // 原作者官网（bot.myarc.icu）已关闭，页面功能失效，改为本地提示页
        // 注意：Page 上的 MaxHeight 视口约束不能删——没有它时本页会撑到内容高度，
        // 内部 ScrollViewer 可滚高度为 0 但仍会吞掉滚轮事件，导致外层滚动器收不到事件、鼠标在正文上滚不动。
    }
}
