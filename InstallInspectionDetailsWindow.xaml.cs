using System.Windows;

namespace NiumaLauncher;

public partial class InstallInspectionDetailsWindow : Window
{
    public InstallInspectionDetailsWindow(string details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(details);

        InitializeComponent();

        // 窗口只展示已经生成的文字，不接触事务服务。
        DetailsTextBox.Text = details;
    }
}