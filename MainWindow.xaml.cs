using System.Windows;
using Microsoft.Win32;
using NiumaLauncher.ViewModel;

namespace NiumaLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();

        // 窗口中的 Binding 默认从这个对象读取属性。
        DataContext = _viewModel;
    }

    private void SelectGameButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Unity 打包后的游戏程序",
            Filter = "游戏程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        // 传入 this，让文件选择窗口归属于当前主窗口。
        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.SelectGame(dialog.FileName);
        }
    }

    private async void LaunchGameButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        // WPF 点击事件使用 async void；
        // 实际业务方法返回 Task，便于等待和处理错误。
        await _viewModel.LaunchGameAsync();
    }

    private async void CheckForUpdatesButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await _viewModel.CheckForUpdatesAsync();
    }

    private async void DownloadPackageButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_viewModel.IsDownloading)
        {
            _viewModel.CancelDownload();
            return;
        }

        await _viewModel.DownloadPackageAsync();
    }
}