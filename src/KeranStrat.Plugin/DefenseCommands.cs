using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private HashSet<string> _defenseAllowed = new(StringComparer.Ordinal);
    private Tactic? _defenseTactic;
    private DefenseSettings? _defenseSettings;
    private int _defenseOwnerSlot = -1;
    private ulong _defenseOwner;
    private bool _defenseLoading;
    private int _defenseGeneration;
    private bool _modeDefense;
    private string _defenseError = "none";
    private DefenseStatistics _defenseStats = new();
    private bool DefenseBusy => _defenseLoading || _defenseState != "idle";
    private static readonly JsonSerializerOptions DefenseJson = new() { WriteIndented = true };
    private static void DefensePracticeFunds(CCSPlayerController player)
    {
        if (player.InGameMoneyServices == null) return;
        player.InGameMoneyServices.Account = 60000;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private void DefenseNotify(string text)
    {
        Server.PrintToConsole("[KeranStrat Defense] " + text);
        var player = NativeReplayEngine.Player(_defenseOwnerSlot);
        if (player is { IsValid: true } && player.SteamID == _defenseOwner) player.PrintToChat("[KeranStrat Defense] " + text);
    }
    private void DefenseRequireIdle()
    {
        if (_modeChanging || _busy || _state != "idle" || _probePhase != 0 || _selfTestPhase != 0)
            throw new InvalidOperationException("当前已有原版录制、回放或切图操作进行中。");
    }
    private void DefenseRequireOwner(CCSPlayerController p)
    {
        if (DefenseBusy && (_defenseOwnerSlot != p.Slot || _defenseOwner != p.SteamID))
            throw new InvalidOperationException("已有其他玩家正在进行 Defense 训练。");
    }
    private string DefenseSettingsPath() => Path.Combine(_dataRoot, "defense", _defenseOwner.ToString(), _defenseTactic!.Id.ToString("N") + ".json");
    private void DefenseSaveSettings()
    {
        if (_defenseTactic == null || _defenseSettings == null) return;
        _defenseSettings.Validate(_defenseTactic);
        string path = DefenseSettingsPath(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path) && Directory.EnumerateFiles(Path.Combine(_dataRoot, "defense"), "*.json", SearchOption.AllDirectories).Take(1024).Count() >= 1024)
            throw new IOException("Defense 场景配置已达到总数上限 1024。");
        string json = JsonSerializer.Serialize(_defenseSettings, DefenseJson);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 65536) throw new IOException("Defense 场景配置过大。");
        File.WriteAllText(path + ".tmp", json); File.Move(path + ".tmp", path, true);
    }
    private void DefenseLoad(CCSPlayerController p, string query)
    {
        string map = Server.MapName; double interval = Server.TickInterval; string gameVersion = _gameVersion; int generation = ++_defenseGeneration;
        _defenseOwner = p.SteamID; _defenseOwnerSlot = p.Slot; _defenseLoading = true;
        ulong owner = _defenseOwner;
        _ = Task.Run(() =>
        {
            try
            {
                var tactic = _store.Load(map, query);
                if (tactic.Team != 2 || tactic.Tracks.Count == 0) throw new InvalidDataException("Defense 只能加载有有效轨迹的 T 方战术。");
                if (tactic.GameVersion != gameVersion || Math.Abs(tactic.TickInterval - interval) > .000001)
                    throw new InvalidDataException("战术游戏版本或 Tick 间隔不兼容。");
                var settings = new DefenseSettings { TacticId = tactic.Id, Map = map, RoundSeconds = Config.DefenseRoundSeconds };
                string path = Path.Combine(_dataRoot, "defense", owner.ToString(), tactic.Id.ToString("N") + ".json");
                if (File.Exists(path))
                {
                    if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Defense 场景配置过大。");
                    settings = JsonSerializer.Deserialize<DefenseSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("Defense 场景无效。");
                }
                settings.Validate(tactic);
                _jobs.Enqueue(() =>
                {
                    if (generation != _defenseGeneration) return;
                    _defenseLoading = false; _defenseTactic = tactic; _defenseSettings = settings;
                    DefenseNotify($"已加载 {tactic.Name}，T 队员：{string.Join(',', tactic.Tracks.Keys.Order())}。切换 CT，!ksd position 设置防守位置。");
                });
            }
            catch (Exception e) { _jobs.Enqueue(() => { if (generation != _defenseGeneration) return; _defenseLoading = false; DefenseNotify("加载失败：" + e.Message); }); }
        });
    }
    private void DefenseCommand(CCSPlayerController? player, CommandInfo cmd)
    {
        void Reply(string s) => cmd.ReplyToCommand("[KeranStrat Defense] " + s);
        string? id = null;
        try { if (player is { IsValid: true, IsBot: false }) id = player.AuthorizedSteamID?.SteamId64.ToString(); } catch (InvalidOperationException) { }
        if (player != null && !AccessPolicy.Allows(Config.DefenseEnabled, _defenseAllowed, id))
        { Reply("你没有权限使用 Defense 模式。"); return; }
        string action = cmd.ArgCount > 1 ? cmd.GetArg(1).ToLowerInvariant() : "help";
        try
        {
            if (action == "help")
            { Reply("!ksd practice | exit | map <地图> | list | load <名称/ID> | position | start | stop | status | repeat on/off | difficulty <1-4> | handoff <1-5> <毫秒/auto> | reload"); return; }
            if (action == "status")
            {
                Reply($"mode={_trainingMode}, changing={_modeChanging}, state={_defenseState}, loading={_defenseLoading}, tactic={_defenseTactic?.Name ?? "none"}, difficulty={_defenseSettings?.Difficulty}, repeat={_defenseSettings?.Repeat}");
                Reply($"总计 {_defenseStats.Rounds} 轮；成功 {_defenseStats.Success}，失败 {_defenseStats.Failure}，超时 {_defenseStats.Timeouts}；error={_defenseError}");
                if (_defenseController != null) foreach (var e in _defenseController.Entries) Reply($"队员 {e.Role}: {e.State}, start={e.StartedTick}, handoff={e.HandoffTick}");
                return;
            }
            if (player == null)
            {
                if (action == "stop") { StopDefense("服务端停止"); Reply("已停止，保留 Practice。"); return; }
                if (action == "reload") { ReloadSettings(); Reply("Defense 配置已重载。"); return; }
                throw new InvalidOperationException("此命令需要 Defense 白名单玩家在游戏内使用。");
            }
            DefenseRequireOwner(player); DefenseRequireIdle();
            if (action == "stop") { StopDefense("主动停止"); Reply("已停止，保留 Practice。"); return; }
            if (action == "repeat")
            {
                if (_defenseSettings == null || cmd.GetArg(2) is not ("on" or "off")) throw new ArgumentException("请先加载战术；用法 !ksd repeat on/off。");
                _defenseSettings.Repeat = cmd.GetArg(2) == "on"; DefenseSaveSettings(); Reply("自动循环 " + cmd.GetArg(2)); return;
            }
            if (action is "exit" or "map")
            {
                StopDefense("切换地图或退出"); _defenseOwner = player.SteamID; _defenseOwnerSlot = player.Slot;
                _modeDefense = true;
                if (action == "exit") ExitTraining(); else ChangeTrainingMap(cmd.GetArg(2));
                Reply("已请求切换，等待地图和规则就绪。"); return;
            }
            if (DefenseBusy) throw new InvalidOperationException("请先 !ksd stop 停止当前训练或加载。");
            _defenseOwner = player.SteamID; _defenseOwnerSlot = player.Slot;
            if (action == "practice")
            {
                if (_trainingMode == "Practice") { _modeDefense = true; DefensePracticeFunds(player); Reply("已进入训练环境，可按 B 购买防守装备。"); return; }
                _modeDefense = true; EnterTraining(); Reply("正在进入训练环境。"); return;
            }
            if (action == "reload") { ReloadSettings(); Reply("Defense 配置已重新加载。"); return; }
            if (_trainingMode != "Practice") throw new InvalidOperationException("请先 !ksd practice。");
            switch (action)
            {
                case "list":
                    _defenseLoading = true; int generation = ++_defenseGeneration; string map = Server.MapName;
                    _ = Task.Run(() =>
                    {
                        try { var list = _store.List(map).Where(t => t.Team == 2).ToArray(); _jobs.Enqueue(() => { if (generation != _defenseGeneration) return; _defenseLoading = false; DefenseNotify(list.Length == 0 ? "当前地图没有 T 方战术。" : string.Join("；", list.Select(t => t.Name + " [" + t.Id.ToString("N") + "]"))); }); }
                        catch (Exception e) { _jobs.Enqueue(() => { if (generation == _defenseGeneration) { _defenseLoading = false; DefenseNotify(e.Message); } }); }
                    }); Reply("正在读取 T 方战术列表。"); break;
                case "load": DefenseLoad(player, Args(cmd, 2)); Reply("正在读取战术。"); break;
                case "position":
                    if (_defenseSettings == null || player.TeamNum != 3 || !player.PawnIsAlive) throw new InvalidOperationException("请先加载战术并在 CT 队出生。");
                    _defenseSettings.Defender = Inventory.Capture(player, 1); DefenseSaveSettings(); Reply("已保存 CT 防守位置、视角和装备。"); break;
                case "start": StartDefense(player); Reply("Defense 训练已启动。"); break;
                case "difficulty":
                    if (_defenseSettings == null || !int.TryParse(cmd.GetArg(2), out int difficulty) || difficulty is < 1 or > 4) throw new ArgumentException("先加载战术；难度为 1～4。");
                    _defenseSettings.Difficulty = difficulty; DefenseSaveSettings(); Reply("BOT 难度已设置为 " + difficulty); break;
                case "handoff":
                    if (_defenseTactic == null || _defenseSettings == null || !int.TryParse(cmd.GetArg(2), out int role) || !_defenseTactic.Tracks.TryGetValue(role, out var track)) throw new ArgumentException("指定队员没有轨迹。");
                    int? ms = null;
                    if (cmd.GetArg(3) != "auto") { if (!int.TryParse(cmd.GetArg(3), out int parsed)) throw new ArgumentException("交接时间为毫秒或 auto。"); ms = parsed; }
                    DefenseBotController.HandoffTicks(ms, track, _defenseTactic.TickInterval);
                    _defenseSettings.HandoffMilliseconds[role] = ms; DefenseSaveSettings(); Reply($"队员 {role} 交接：{ms?.ToString() ?? "auto"}（从自身轨迹开始计算）。"); break;
                default: throw new ArgumentException("未知 Defense 指令，!ksd 查看帮助。");
            }
        }
        catch (Exception e) { _defenseError = e.Message; Reply(e.Message); }
    }
}
