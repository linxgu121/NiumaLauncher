using System.Windows;
using NiumaLauncher.Models;


namespace NiumaLauncher;

public partial class InstallPlanPreviewWindow : Window
{
    public InstallPlanPreviewWindow(GameInstallPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        InitializeComponent();

        // 这里只把计划转换为展示文字，不读取或修改游戏文件。
        PlanTextBox.Text = string.Join(
            Environment.NewLine,
            new[]
            {
                $"操作编号：{plan.OperationId:N}",
                $"游戏身份：{plan.SourcePackage.GameId}",
                string.Empty,

                $"原版本：{plan.CurrentVersion}" +
                $"（构建 {plan.CurrentBuildNumber}）",

                $"目标版本：{plan.SourcePackage.Version}" +
                $"（构建 {plan.SourcePackage.BuildNumber}）",

                string.Empty,
                $"正式程序：{plan.GameExecutablePath}",
                $"正式目录：{plan.GameDirectoryPath}",

                string.Empty,
                $"工作区（尚未创建）：{plan.WorkspaceDirectoryPath}",
                $"候选目录（尚未创建）：{plan.CandidateDirectoryPath}",
                $"备份目录（尚未创建）：{plan.BackupDirectoryPath}",
                $"失败隔离目录（尚未创建）：{plan.FailedNewDirectoryPath}",

                string.Empty,
                $"来源 ZIP：{plan.SourcePackage.FilePath}",

                string.Empty,
                "以上内容只是本次检查得到的计划",
                "当前确认不执行安装；正式执行时仍须重新检查目录和构建"
            });
    }

    private void ConfirmPlanButton_Click(object sender, RoutedEventArgs e)
    {
        // 按钮禁用只是界面提示，事件入口仍检查确认条件。
        if (DirectoryScopeCheckBox.IsChecked != true)
        {
            return;
        }

        // 只返回用户选择，不在窗口中调用安装服务。
        // 当前窗口由 ShowDialog 打开，设置结果会关闭对话框。
        DialogResult = true;
    }
}