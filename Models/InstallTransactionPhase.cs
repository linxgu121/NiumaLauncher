namespace NiumaLauncher.Models;

/// <summary>
/// 安装事务的登记阶段，不是界面状态或下载进度。
/// </summary>
public enum InstallTransactionPhase
{
    // 保留 0 为无效值，避免遗漏字段时被当作合法阶段。
    Unknown = 0,

    // 已登记准备意图。
    // 候选可能尚未创建、部分写入，或已完成但记录尚未更新。
    PreparingCandidate = 10,

    // 候选通过检查后的登记阶段，不代表已替换正式游戏。
    CandidateReady = 20,

    // 已登记将旧游戏目录移入备份的意图。
    // 不代表备份成功；中断后必须检查目录实际位置。
    BackupMovePending = 30,

    // 已登记将候选目录移入正式游戏位置的意图。
    // 不代表候选已落位；中断后仍需检查实际目录布局。
    CandidateMovePending = 40
}