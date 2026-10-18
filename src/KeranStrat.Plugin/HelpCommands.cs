using CounterStrikeSharp.API.Modules.Commands;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    // Read-only help is available before authorization and never changes server state.
    private static void ReplyHelp(CommandInfo command, string mode)
    {
        string title = mode switch
        {
            "ksv" => "KSV 双阵营 BOT 模拟",
            "ksd" => "KSD 真人 CT 协同防守",
            _ => "KS 战术录制与回放"
        };
        string quick = mode switch
        {
            "ksv" => "入门：!ksv practice → new 名称 → record t/ct 1 → stop → save → play",
            "ksd" => "入门：!ksd practice → CT 阵营 → load 战术 → position → start；队友 join 后各自 position",
            _ => "入门：!ks practice → new 名称 → record 1 → stop → save → play"
        };
        command.ReplyToCommand($"[KeranStrat] {title}");
        command.ReplyToCommand($"[KeranStrat] 获取使用方法、快速上手及完整指令，请前往：https://cs.jinhsi.cn/help");
        command.ReplyToCommand($"[KeranStrat] {quick}（后续指令需保留 !{mode} 前缀）");
        command.ReplyToCommand($"[KeranStrat] stop 停止当前操作；!{mode} exit 退出训练并恢复死斗。训练操作需对应模式权限。");
    }
}
