using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using NiumaLauncher.Models;
using NiumaLauncher.ViewModel;

namespace NiumaLauncher;

public partial class MainWindow : Window
{
    private readonly LauncherViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();

        // 先绑定数据，让界面读取初始的检查中状态。
        DataContext = _viewModel;

        // 窗口加载后再开始异步初始化。
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 本窗口只触发一次；ViewModel 内部也会复用初始化任务。
        Loaded -= MainWindow_Loaded;

        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            // 普通检查失败已由 ViewModel 转为待处理状态。
            // 到这里的是继续传播的未预期错误，不能忽略后继续使用。
            System.Diagnostics.Debug.WriteLine(exception);

            // 等待期间窗口可能已经被用户关闭。
            if (!IsLoaded)
            {
                return;
            }

            MessageBox.Show(
                this,
                "启动初始化发生未预期错误，启动器将关闭。\n\n" +
                $"{exception.GetType().Name}: {exception.Message}",
                "初始化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Close();
        }
    }

    private void ViewStartupInspectionButton_Click(
    object sender,
    RoutedEventArgs e)
    {
        if (!IsLoaded || !_viewModel.CanViewStartupInspection)
        {
            return;
        }

        string details = _viewModel.CreateStartupInspectionDetails();

        if (string.IsNullOrWhiteSpace(details))
        {
            return;
        }

        // 每次创建新窗口，不复用已经关闭的实例。
        var detailsWindow = new InstallInspectionDetailsWindow(details)
        {
            Owner = this
        };

        // 关闭详情后不修改任何业务状态。
        detailsWindow.ShowDialog();
    }

    private async void RecheckStartupInspectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            await _viewModel.RecheckStartupInspectionAsync();
        }
        catch (Exception exception)
        {
            // 普通检查失败由 ViewModel 保留为待处理状态。
            // 这里只处理继续传播的未预期错误。
            System.Diagnostics.Debug.WriteLine(exception);

            if (!IsLoaded)
            {
                return;
            }

            MessageBox.Show(
                this,
                "重新检查发生未预期错误，启动器将关闭。\n\n" +
                $"{exception.GetType().Name}: {exception.Message}",
                "安装检查异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Close();
        }
    }

    private async void SelectGameButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!IsLoaded || !_viewModel.CanSelectGame)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 Unity 打包后的游戏程序",
            Filter = "游戏程序 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            // ViewModel 会先检查，再决定是否应用和保存新路径。
            await _viewModel.SelectGameAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            // 普通检查失败已经转为待处理状态；
            // 到这里的是继续传播的未预期错误。
            System.Diagnostics.Debug.WriteLine(exception);

            if (!IsLoaded)
            {
                return;
            }

            MessageBox.Show(
                this,
                "选择游戏时发生未预期错误，启动器将关闭。\n\n" +
                $"{exception.GetType().Name}: {exception.Message}",
                "安装检查异常",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Close();
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
        // 下载与暂存是不同阶段，保留各自的按钮入口。
        if (_viewModel.IsDownloading)
        {
            _viewModel.CancelDownload();
            return;
        }

        await _viewModel.DownloadPackageAsync();
    }

    private async void PreparePackageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsPreparingPackage)
        {
            _viewModel.CancelPreparation();
            return;
        }

        await _viewModel.PreparePackageAsync();
    }

    private async void PreviewInstallPlanButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        GameInstallPlan? plan =
            await _viewModel.CreateInstallPlanPreviewAsync();

        // 检查期间用户可能已经关闭主窗口。
        if (plan is null || !IsLoaded)
        {
            return;
        }

        var previewWindow = new InstallPlanPreviewWindow(plan)
        {
            Owner = this
        };

        // 只有明确返回 true 才表示确认。
        // 取消、Esc 和关闭窗口都直接结束本次流程。
        if (previewWindow.ShowDialog() != true)
        {
            return;
        }

        // 当前只完成确认交互，尚未开放真实安装入口。
        // 不保存长期授权，也不在这里重新生成另一份计划。
        MessageBox.Show(
            this,
            "本次计划已确认，但当前阶段尚未开放真实安装。\n\n" +
            "没有执行目录切换。以后正式安装时，需要重新生成并确认当次计划。",
            "计划确认完成",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (!_viewModel.IsInstalling)
        {
            return;
        }

        e.Cancel = true;

        MessageBox.Show(
            this,
            "安装或安装后的复核尚未结束，请等待结果后再关闭启动器。",
            "正在安装",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
