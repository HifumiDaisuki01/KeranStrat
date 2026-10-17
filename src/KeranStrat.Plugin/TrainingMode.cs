using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private string _trainingMode = "Deathmatch";
    private bool _modeChanging;
    private bool _targetPractice;
    private string _targetMap = "";
    private bool _targetMapStarted;
    private int _modeApplyTick;
    private bool _trainingSetupIssued;
    private int _trainingReadyAt;
    private int _trainingStableSince;
    private long _modeRequestTime;
    private readonly List<string> _modeReports = [];
    private FileSystemWatcher? _configWatcher;
    private long _configChangedAt;

    private string ConfigurationPath => Path.GetFullPath(Path.Combine(ModuleDirectory, "..", "..", "configs", "plugins", "KeranStrat", "KeranStrat.json"));
    private void WatchConfiguration()
    {
        _configWatcher = new FileSystemWatcher(Path.GetDirectoryName(ConfigurationPath)!, Path.GetFileName(ConfigurationPath))
            { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
        FileSystemEventHandler changed = (_, _) => Interlocked.Exchange(ref _configChangedAt, Stopwatch.GetTimestamp());
        _configWatcher.Changed += changed; _configWatcher.Created += changed;
        _configWatcher.Renamed += (_, _) => Interlocked.Exchange(ref _configChangedAt, Stopwatch.GetTimestamp());
        _configWatcher.EnableRaisingEvents = true;
    }
    private void ReloadConfigurationIfChanged()
    {
        long changed = Interlocked.Read(ref _configChangedAt);
        if (changed == 0 || _modeChanging || _busy || _state != "idle" || Stopwatch.GetElapsedTime(changed).TotalSeconds < .5) return;
        Interlocked.Exchange(ref _configChangedAt, 0);
        try { ReloadSettings(); Log("配置文件已安全热重载。"); }
        catch (Exception e) { Log("配置文件重载失败，保留原有效配置: " + e.Message); }
    }

    private static void SetRules(bool practice)
    {
        // Use the engine ConVar setter, not a client-issued game_type command; then reload the map.
        var type = ConVar.Find("game_type") ?? throw new InvalidOperationException("game_type 不可用。");
        var mode = ConVar.Find("game_mode") ?? throw new InvalidOperationException("game_mode 不可用。");
        type.SetValue(practice ? 0 : 1); mode.SetValue(practice ? 1 : 2);
        if (type.StringValue != (practice ? "0" : "1") || mode.StringValue != (practice ? "1" : "2"))
            throw new InvalidOperationException("引擎拒绝修改游戏规则，未假定切换成功。");
    }
    private static string ValidateMap(string map)
    {
        if (!Regex.IsMatch(map, "^de_[a-z0-9_]{1,48}$")) throw new ArgumentException("地图必须为有效 de_ 地图名。");
        string[] candidates = [Path.Combine(Server.GameDirectory, "maps", map + ".vpk"), Path.Combine(Server.GameDirectory, "csgo", "maps", map + ".vpk")];
        if (!candidates.Any(File.Exists)) throw new FileNotFoundException("服务器没有安装这个地图。");
        return map;
    }
    private void EnterTraining()
    {
        if (_modeChanging || _busy || _state != "idle") throw new InvalidOperationException("已有操作进行中。");
        if (_trainingMode == "Practice") throw new InvalidOperationException("已经处于训练模式。");
        if (!Bind()) throw new InvalidOperationException("BotController 未就绪，未切换模式。");
        string map = ValidateMap(Server.MapName);
        _practice.Enter();
        try { SwitchMode(map, true); }
        catch { _practice.Exit(); throw; }
    }
    private void SaveBeforeSwitch(Action next)
    {
        if (_busy) throw new InvalidOperationException("正在保存文件，请稍后。");
        if (_state == "recording") FinishRecording();
        Abort("准备切换训练地图或退出", false);
        if (_tactic == null) { next(); return; }
        var snapshot = _tactic.Snapshot();
        Background(() => { _store.Save(snapshot); return true; }, _ => { Notify("当前战术已自动保存。"); next(); });
    }
    private void ChangeTrainingMap(string map)
    {
        if (_trainingMode != "Practice") throw new InvalidOperationException("请先 !ks practice。");
        map = ValidateMap(map);
        SaveBeforeSwitch(() => SwitchMode(map, true));
    }
    private void ExitTraining()
    {
        if (_trainingMode != "Practice" && !_practice.Active) throw new InvalidOperationException("当前已经是普通死斗。");
        SaveBeforeSwitch(() => SwitchMode(ValidateMap(Config.DefaultMap), false));
    }
    private void SwitchMode(string map, bool practice)
    {
        if (_modeChanging) throw new InvalidOperationException("模式切换中。");
        _targetMap = map; _targetPractice = practice; _targetMapStarted = false;
        _trainingSetupIssued = false;
        _modeRequestTime = Stopwatch.GetTimestamp(); _modeChanging = true;
        Server.PrintToChatAll(practice ? "[KeranStrat] 即将载入战术训练地图，请稍候。" : "[KeranStrat] 训练结束，即将恢复 Dust2 死斗。");
        try { SetRules(practice); Server.ExecuteCommand("map " + map); }
        catch { _modeChanging = false; throw; }
    }
    private void TrainingMapStarted(string map)
    {
        _tactic = null; _anchor = null;
        _lastRecordedRole = 0; _hudUntil = 0; _hudNextTick = 0; _trainingSetupIssued = false;
        if (_modeChanging)
        {
            _targetMapStarted = map == _targetMap;
            _modeApplyTick = Server.TickCount + 16;
        }
        else if (_trainingMode == "Practice" && _practice.Active)
        {
            // An external map command still interrupts motion and rechecks training rules.
            _targetMap = map; _targetPractice = true; _targetMapStarted = true; _modeChanging = true;
            _modeRequestTime = Stopwatch.GetTimestamp(); _modeApplyTick = Server.TickCount + 16;
        }
    }
    private bool TrainingTick()
    {
        if (!_modeChanging) return false;
        if (Stopwatch.GetElapsedTime(_modeRequestTime).TotalSeconds > 60)
        {
            _modeChanging = false; _lastError = "模式/地图加载超时；未声明切换成功。请检查服务器并使用 !ks exit。";
            Notify(_lastError); return true;
        }
        if (!_targetMapStarted || Server.TickCount < _modeApplyTick) return true;
        string type = ConVar.Find("game_type")?.StringValue ?? "missing";
        string mode = ConVar.Find("game_mode")?.StringValue ?? "missing";
        if (Server.MapName != _targetMap || type != (_targetPractice ? "0" : "1") || mode != (_targetPractice ? "1" : "2"))
        {
            if (Stopwatch.GetElapsedTime(_modeRequestTime).TotalSeconds < 10) return true;
            _modeChanging = false;
            _lastError = $"实际模式不符：map={Server.MapName} game_type={type} game_mode={mode}；未假定训练成功。";
            Notify(_lastError); return true;
        }
        if (_targetPractice)
        {
            if (!_trainingSetupIssued)
            {
                _practice.Apply();
                // Queue after training convars, then wait for the engine's real warmup transition.
                Server.ExecuteCommand("mp_warmup_end");
                Server.ExecuteCommand("mp_restartgame 1");
                _trainingSetupIssued = true;
                _trainingStableSince = 0;
                _trainingReadyAt = Server.TickCount + ReplayCoordinator.ToTicks(5000, Server.TickInterval);
                return true;
            }
            if (Server.TickCount < _trainingReadyAt) return true;
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            if (rules == null || rules.WarmupPeriod || rules.FreezePeriod) { _trainingStableSince = 0; return true; }
            if (_trainingStableSince == 0) _trainingStableSince = Server.TickCount;
            if (Server.TickCount - _trainingStableSince < ReplayCoordinator.ToTicks(2000, Server.TickInterval)) return true;
            _trainingMode = "Practice";
        }
        else
        {
            _practice.Exit(); _trainingMode = "Deathmatch"; _owner = 0; _ownerSlot = -1;
        }
        _modeChanging = false;
        string result = $"mode={_trainingMode} map={Server.MapName} game_type={type} game_mode={mode}";
        _modeReports.Add(result); Notify("已核实 " + result);
        Server.PrintToChatAll(_targetPractice ? "[KeranStrat] 战术训练已就绪。结束后 !ks exit 恢复 Dust2 死斗。" : "[KeranStrat] 已恢复 Dust2 死斗。");
        File.WriteAllText(Path.Combine(_dataRoot, "mode-test-report.json"), JsonSerializer.Serialize(_modeReports));
        return true;
    }
    private void ReloadSettings()
    {
        if (_modeChanging || _state != "idle" || _busy) throw new InvalidOperationException("请先停止当前操作再重载配置。");
        var settings = JsonSerializer.Deserialize<KeranStratConfig>(File.ReadAllText(ConfigurationPath), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? throw new InvalidDataException("配置为空。");
        if (settings.DataDirectory != Config.DataDirectory) throw new InvalidOperationException("在线重载不能迁移存储目录。");
        OnConfigParsed(settings);
        _store = new FileTacticStore(_dataRoot, Config.MaxTacticsPerMap, Config.MaxFileMegabytes * 1024L * 1024, MaxTicks, Config.MaxStorageMegabytes * 1024L * 1024);
        if (!_practice.Active) _practice = new PracticeEnvironment(Path.Combine(_dataRoot, "practice-recovery.json"), Config.PracticeCvars);
        if (!Config.Enabled && _trainingMode == "Practice") ExitTraining();
    }
    private static readonly HashSet<string> PrimaryWeapons = new(StringComparer.Ordinal)
    {
        "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_awp", "weapon_ssg08",
        "weapon_aug", "weapon_sg556", "weapon_famas", "weapon_galilar", "weapon_g3sg1", "weapon_scar20",
        "weapon_mac10", "weapon_mp9", "weapon_mp7", "weapon_mp5sd", "weapon_ump45", "weapon_p90", "weapon_bizon",
        "weapon_nova", "weapon_xm1014", "weapon_mag7", "weapon_sawedoff", "weapon_m249", "weapon_negev"
    };
    private static bool IsGrenade(string name) => name is "weapon_smokegrenade" or "weapon_flashbang" or
        "weapon_hegrenade" or "weapon_molotov" or "weapon_incgrenade" or "weapon_decoy";
    private void GivePracticeKit(CCSPlayerController player, string? requestedPrimary = null)
    {
        string? selected = requestedPrimary == null ? null : "weapon_" + requestedPrimary.ToLowerInvariant().Replace("weapon_", "");
        if (selected != null && !PrimaryWeapons.Contains(selected))
            throw new ArgumentException("未知主武器。示例：!ks kit awp / ak47 / m4a1_silencer / ssg08。");
        var kit = Inventory.Capture(player, 1);
        var items = kit.Equipment.Where(e => !IsGrenade(e.ClassName) && (selected == null || !PrimaryWeapons.Contains(e.ClassName))).ToList();
        if (!items.Any(e => PrimaryWeapons.Contains(e.ClassName)))
            items.Add(new Equipment(selected ?? (player.TeamNum == 2 ? "weapon_ak47" : "weapon_m4a1_silencer")));
        if (!items.Any(e => e.ClassName.StartsWith("weapon_knife", StringComparison.Ordinal))) items.Add(new Equipment("weapon_knife"));
        if (!items.Any(e => e.ClassName is "weapon_glock" or "weapon_usp_silencer" or "weapon_hkp2000" or
            "weapon_deagle" or "weapon_revolver" or "weapon_p250" or "weapon_fiveseven" or "weapon_tec9" or "weapon_cz75a" or "weapon_elite"))
            items.Add(new Equipment(player.TeamNum == 2 ? "weapon_glock" : "weapon_usp_silencer"));
        foreach (var name in Config.PracticeGrenades.Distinct(StringComparer.Ordinal))
        {
            items.Add(new Equipment(name == "weapon_molotov" && player.TeamNum == 3 ? "weapon_incgrenade" : name));
            if (name == "weapon_flashbang") items.Add(new Equipment(name));
        }
        kit.Equipment = items.ToArray();
        kit.Ammo = []; // Giving the configured kit initializes new grenade stack counts.
        kit.Health = 100; kit.Armor = 100; kit.Helmet = true;
        Inventory.Restore(player, kit);
        if (player.InGameMoneyServices != null)
        {
            player.InGameMoneyServices.Account = 60000;
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
    }
}
