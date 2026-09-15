namespace NiumaLauncher.Models;

/// <summary>
/// 根据本轮检查结果生成的恢复建议。
/// 不代表动作已经执行，也不授予目录操作权限。
/// </summary>
internal enum InstallRecoveryRecommendation
{
    // 默认要求人工检查，避免未初始化值进入自动处理。
    ManualReview = 0,

    // 建议保留原构建，不代表可以忽略未完成事务。
    KeepOriginalBuild = 10,

    // 建议进入旧备份恢复流程，尚未移动目录。
    RestoreOriginalBackup = 20,

    // 建议进入目标安装完成确认流程，尚未登记 Completed。
    ConfirmTargetInstallation = 30
}