namespace NiumaLauncher.Models;

/// <summary>
/// 未完成安装中，与日志阶段相容的目录布局。
/// 只描述位置组合，不表示构建已验证或允许执行恢复。
/// </summary>
internal enum InstallRecoveryLayout
{
    // 信息不足、读取异常或布局无法解释。
    Unknown = 0,

    // 正式目录存在，本次工作区尚不存在。
    GameOnly = 10,

    // 正式目录和工作区存在，候选可能尚未创建或写完。
    PreparationWorkspace = 20,

    // 正式目录和候选目录存在，备份不存在。
    GameAndCandidate = 30,

    // 正式目录不存在，候选和备份存在。
    CandidateAndBackup = 40,

    // 正式目录和备份存在，候选目录不存在。
    GameAndBackup = 50
}