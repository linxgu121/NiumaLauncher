namespace NiumaLauncher.Models;

/// <summary>
/// 本轮恢复检查中，目标新构建的核对结果。
/// 不是持久化事务阶段，也不表示允许执行恢复。
/// </summary>
internal enum InstallRecoveryTargetStatus
{
    // 默认无效值，不能按成功处理。
    Unknown = 0,

    // 当前布局下，本轮没有检查目标内容。
    NotChecked = 10,

    // EXE 和清单通过现有的目标身份及基础条件核对。
    MatchesExpected = 20,

    // 尝试检查，但未能确认；必须结合诊断原因处理。
    NotVerified = 30
}