using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KeranStrat.Plugin;

internal sealed class PracticeEnvironment(string recoveryFile, IReadOnlyDictionary<string, string>? settings = null)
{
    private Dictionary<string, string>? _before;
    public bool Active => _before != null;
    public static readonly Dictionary<string, string> Defaults = new()
    {
        ["sv_cheats"] = "1", ["mp_autoteambalance"] = "0", ["mp_limitteams"] = "0",
        ["mp_freezetime"] = "0", ["mp_ignore_round_win_conditions"] = "1",
        ["mp_roundtime"] = "60", ["mp_roundtime_defuse"] = "60",
        ["bot_quota_mode"] = "normal", ["bot_quota"] = "0", ["bot_dont_shoot"] = "1", ["bot_join_after_player"] = "0",
        ["mp_respawn_on_death_t"] = "0", ["mp_respawn_on_death_ct"] = "0",
        ["sv_hibernate_when_empty"] = "0", ["mp_solid_teammates"] = "0", ["ammo_grenade_limit_total"] = "6",
        ["ammo_grenade_limit_flashbang"] = "2", ["mp_buytime"] = "9999", ["mp_buy_anywhere"] = "1",
        ["mp_maxmoney"] = "60000", ["mp_startmoney"] = "60000", ["mp_afterroundmoney"] = "60000",
        ["mp_team_intro_time"] = "0", ["mp_warmuptime"] = "5", ["mp_warmup_pausetimer"] = "0",
        ["mp_warmup_online_enabled"] = "0", ["mp_warmup_offline_enabled"] = "0",
        ["mp_warmuptime_all_players_connected"] = "0", ["sv_warmup_to_freezetime_delay"] = "0",
        ["mp_halftime"] = "0", ["mp_maxrounds"] = "999", ["mp_timelimit"] = "0", ["mp_match_can_clinch"] = "0"
    };
    public void Recover()
    {
        if (!File.Exists(recoveryFile)) return;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(recoveryFile))
            ?? throw new InvalidDataException("练习配置恢复文件无效。");
        if (values.Keys.Any(k => !Defaults.ContainsKey(k)) || values.Values.Any(v => !Regex.IsMatch(v, "^[a-zA-Z0-9_.+-]{1,32}$")))
            throw new InvalidDataException("练习配置恢复文件含非法设置。");
        _before = values; Exit();
    }
    public void Enter()
    {
        if (Active) return;
        var desired = settings ?? Defaults;
        if (desired.Count != Defaults.Count || desired.Keys.Any(k => !Defaults.ContainsKey(k)) ||
            desired.Values.Any(v => !Regex.IsMatch(v, "^[a-zA-Z0-9_.+-]{1,32}$")))
            throw new InvalidOperationException("练习参数必须使用完整的白名单配置。");
        var before = new Dictionary<string, string>();
        foreach (var key in Defaults.Keys)
        {
            var cv = ConVar.Find(key);
            if (cv == null) throw new InvalidOperationException($"缺少练习参数 {key}，未修改环境。");
            before[key] = cv.StringValue;
            if (!Regex.IsMatch(before[key], "^[a-zA-Z0-9_.+-]{1,32}$")) throw new InvalidOperationException("练习参数原值不可安全恢复。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryFile)!);
        File.WriteAllText(recoveryFile + ".tmp", JsonSerializer.Serialize(before));
        File.Move(recoveryFile + ".tmp", recoveryFile, true);
        _before = before;
        Apply();
    }
    public void Apply()
    {
        if (!Active) throw new InvalidOperationException("没有可恢复的练习快照。");
        var desired = settings ?? Defaults;
        foreach (var (key, value) in desired) Server.ExecuteCommand($"{key} {value}");
    }
    public void Exit()
    {
        if (_before == null) return;
        // Restore sv_cheats last so it cannot prevent restoring a protected cvar.
        foreach (var (key, value) in _before.Where(kv => kv.Key != "sv_cheats")) Server.ExecuteCommand($"{key} {value}");
        if (_before.TryGetValue("sv_cheats", out var cheats)) Server.ExecuteCommand($"sv_cheats {cheats}");
        _before = null;
        if (File.Exists(recoveryFile)) File.Delete(recoveryFile);
    }
}
