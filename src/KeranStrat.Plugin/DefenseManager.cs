using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private string _defenseState = "idle";
    private NativeReplayEngine? _defenseEngine;
    private DefenseBotController? _defenseController;
    private readonly List<(int Role, int Slot, nint Handle, int UserId)> _defenseBots = [];
    private readonly HashSet<int> _defenseSpawnBaseline = [];
    private Dictionary<string, string>? _defenseCvars;
    private int _defenseDeadline, _defenseRoundStart, _defenseNextRound, _defenseReadyAt;
    private readonly Dictionary<uint, string> _defenseTemporary = [];
    private readonly HashSet<uint> _defenseEntityBaseline = [];
    private readonly HashSet<uint> _defenseParticipantHandles = [];
    private readonly HashSet<int> _defenseExternalBots = [];
    private int _defenseEntityScanAt;
    private string[] _defenseEntityClasses = DefenseTemporaryClasses;
    private static readonly string[] DefenseTemporaryClasses = ["smokegrenade_projectile", "flashbang_projectile", "hegrenade_projectile", "molotov_projectile", "decoy_projectile", "inferno", "weapon_ak47", "weapon_m4a1", "weapon_m4a1_silencer", "weapon_awp", "weapon_ssg08", "weapon_glock", "weapon_usp_silencer", "weapon_smokegrenade", "weapon_flashbang", "weapon_hegrenade", "weapon_molotov", "weapon_incgrenade", "weapon_decoy"];

    private CCSPlayerController? DefensePlayer()
    {
        var p = NativeReplayEngine.Player(_defenseOwnerSlot);
        return p is { IsValid: true } && p.SteamID == _defenseOwner ? p : null;
    }
    private void StartDefense(CCSPlayerController player)
    {
        if (!Config.DefenseEnabled || DefenseBusy) throw new InvalidOperationException("Defense 当前不可启动。");
        DefenseRequireIdle();
        if (_trainingMode != "Practice" || !_practice.Active || _modeChanging || player.TeamNum != 3 || !player.PawnIsAlive)
            throw new InvalidOperationException("需要 Practice、CT 阵营并已出生。");
        if (_defenseTactic == null || _defenseTactic.Map != Server.MapName || _defenseTactic.Team != 2 || _defenseTactic.Tracks.Count == 0 || _defenseSettings?.Defender == null)
            throw new InvalidOperationException("先 !ksd load T方战术，再 !ksd position 设置 CT 防守位置。");
        _defenseTactic.Validate(MaxTicks); _defenseSettings.Validate(_defenseTactic);
        if (!Bind()) throw new InvalidOperationException("BotController 未就绪。");
        _defenseOwner = player.SteamID; _defenseOwnerSlot = player.Slot;
        _modeDefense = true;
        // Keep the chosen position, but capture the loadout the CT is wearing when start is pressed.
        var equipment = Inventory.Capture(player, 1); equipment.Initial = _defenseSettings.Defender.Initial;
        equipment.Health = 100; _defenseSettings.Defender = equipment; DefenseSaveSettings();
        var values = new Dictionary<string, string>();
        foreach (string key in new[] { "bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota", "bot_quota_mode", "mp_respawn_immunitytime" })
            values[key] = ConVar.Find(key)?.StringValue ?? throw new InvalidOperationException("缺少 Defense 参数 " + key);
        string recovery = Path.Combine(_dataRoot, "defense-recovery.json");
        File.WriteAllText(recovery + ".tmp", JsonSerializer.Serialize(values)); File.Move(recovery + ".tmp", recovery, true);
        _defenseCvars = values; _defenseEngine = new NativeReplayEngine(_api!);
        _defenseExternalBots.Clear();
        foreach (var bot in Utilities.GetPlayers()) if (bot.IsValid && bot.IsBot && bot.UserId is int id) _defenseExternalBots.Add(id);
        _defenseController = new DefenseBotController(_defenseEngine);
        _defenseError = "none";
        try
        {
            Server.ExecuteCommand("bot_dont_shoot 0"); Server.ExecuteCommand("bot_difficulty " + (_defenseSettings.Difficulty - 1));
            Server.ExecuteCommand("bot_stop 0"); Server.ExecuteCommand("bot_zombie 0");
            Server.ExecuteCommand("bot_quota_mode normal"); Server.ExecuteCommand("mp_respawn_immunitytime 0");
            BeginDefenseRound();
        }
        catch { StopDefense("启动失败"); throw; }
    }
    private void BeginDefenseRound()
    {
        var player = DefensePlayer() ?? throw new InvalidOperationException("防守玩家已离线。");
        if (player.TeamNum != 3) throw new InvalidOperationException("防守玩家已离开 CT 队伍。");
        CleanupDefenseBots(); CleanupDefenseEntities();
        if (!player.PawnIsAlive) { player.Respawn(); _defenseState = "respawning"; _defenseDeadline = Server.TickCount + 640; return; }
        Inventory.Restore(player, _defenseSettings!.Defender!);
        if (player.PlayerPawn.Value != null) player.PlayerPawn.Value.GunGameImmunity = false;
        _defenseEntityBaseline.Clear();
        _defenseParticipantHandles.Clear(); _defenseParticipantHandles.Add(player.EntityHandle.Raw);
        _defenseEntityClasses = DefenseTemporaryClasses.Concat(_defenseTactic!.Tracks.Values.SelectMany(t => t.Equipment).Select(e => e.ClassName))
            .Concat(_defenseSettings.Defender!.Equipment.Select(e => e.ClassName)).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in _defenseEntityClasses)
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name)) _defenseEntityBaseline.Add(entity.EntityHandle.Raw);
        if (player.PlayerPawn.Value?.WeaponServices is { } weapons)
            foreach (var handle in weapons.MyWeapons)
                if (handle.Value is { IsValid: true } item) _defenseTemporary[item.EntityHandle.Raw] = item.DesignerName;
        _defenseSpawnBaseline.Clear();
        foreach (var p in Utilities.GetPlayers()) if (p.IsValid && p.UserId is int id) _defenseSpawnBaseline.Add(id);
        _defenseBots.Clear();
        foreach (int role in _defenseTactic!.Tracks.Keys.Order())
        { _defenseBots.Add((role, -1, 0, -1)); Server.ExecuteCommand("bot_add_t"); }
        Server.ExecuteCommand("bot_quota " + (_defenseBots.Count + Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot && p.UserId is int id && _defenseExternalBots.Contains(id))));
        _defenseReadyAt = 0; _defenseDeadline = Server.TickCount + DefenseTicks(15000); _defenseState = "preparing";
        DefenseNotify("准备第 " + (_defenseStats.Rounds + 1) + " 轮，正在生成 T 方 BOT。");
    }
    private static int DefenseTicks(int milliseconds) => checked((int)Math.Ceiling(milliseconds / (1000d * Server.TickInterval)));
    private void DefensePrepareTick()
    {
        if (Server.TickCount > _defenseDeadline) throw new TimeoutException("Defense BOT 创建或出生超时。");
        if (_defenseReadyAt != 0)
        {
            if (Server.TickCount < _defenseReadyAt) return;
            int start = Server.TickCount + 1;
            _defenseController!.Begin(start, _defenseBots.Select(b =>
            {
                var track = _defenseTactic!.Tracks[b.Role];
                return new DefenseBotEntry(b.Role, b.Slot, DefenseTicks(track.OffsetMs), track.Ticks.Length,
                    DefenseBotController.HandoffTicks(_defenseSettings!.HandoffMilliseconds.GetValueOrDefault(b.Role), track, _defenseTactic.TickInterval),
                    _defenseSettings.HandoffMilliseconds.GetValueOrDefault(b.Role) == null);
            }).ToArray());
            _defenseRoundStart = start; _defenseState = "running"; DefenseNotify("开始防守！"); return;
        }
        var players = Utilities.GetPlayers();
        for (int i = 0; i < _defenseBots.Count; i++)
        {
            var b = _defenseBots[i]; if (b.Slot >= 0) continue;
            var bot = players.FirstOrDefault(p => p.IsValid && p.IsBot && p.TeamNum == 2 && p.UserId is int id && !_defenseSpawnBaseline.Contains(id));
            if (bot == null) continue;
            if (!bot.PawnIsAlive) { bot.Respawn(); continue; }
            _defenseSpawnBaseline.Add(bot.UserId!.Value); _defenseBots[i] = (b.Role, bot.Slot, bot.Handle, bot.UserId.Value);
            _defenseParticipantHandles.Add(bot.EntityHandle.Raw);
            _defenseEngine!.Prepare(bot, _defenseTactic!.Tracks[b.Role]);
        }
        if (_defenseBots.All(b => b.Slot >= 0))
        { _defenseReadyAt = Server.TickCount + DefenseTicks(3000); DefenseNotify("3 秒后开始防守。"); }
    }
    private void DefenseTick()
    {
        if (_defenseState == "idle") return;
        try
        {
            var player = DefensePlayer();
            if (player == null || player.TeamNum != 3) throw new InvalidOperationException("防守玩家离线或离开 CT 队伍。");
            if (!Config.DefenseEnabled || (!player.IsBot && !AccessPolicy.Allows(true, _defenseAllowed, player.AuthorizedSteamID?.SteamId64.ToString())))
                throw new InvalidOperationException("Defense 已关闭或权限已移除。");
            if (Server.TickCount >= _defenseEntityScanAt)
            { TrackDefenseEntities(); _defenseEntityScanAt = Server.TickCount + DefenseTicks(200); }
            switch (_defenseState)
            {
                case "respawning":
                    if (Server.TickCount > _defenseDeadline) throw new TimeoutException("CT 复活超时。");
                    if (player.PawnIsAlive) BeginDefenseRound(); break;
                case "preparing":
                    if (!player.PawnIsAlive) throw new InvalidOperationException("准备期间防守玩家死亡，请重新开始。");
                    DefensePrepareTick(); break;
                case "running":
                    _defenseController!.Tick(Server.TickCount);
                    var outcome = DefenseStatistics.Evaluate(player.PawnIsAlive, _defenseController.DeadCount, _defenseBots.Count,
                        Server.TickCount - _defenseRoundStart >= DefenseTicks(_defenseSettings!.RoundSeconds * 1000));
                    if (outcome != null) FinishDefenseRound(outcome.Value); break;
                case "between":
                    if (Server.TickCount >= _defenseNextRound)
                    { if (_defenseSettings!.Repeat) BeginDefenseRound(); else StopDefense("本轮结束，自动循环关闭"); }
                    break;
            }
        }
        catch (Exception e) { _defenseError = e.Message; DefenseNotify("训练中止：" + e.Message); StopDefense("异常停止"); }
    }
    private void FinishDefenseRound(DefenseResult result)
    {
        _defenseStats.Finish(result);
        string label = result == DefenseResult.Success ? "防守成功" : result == DefenseResult.Failure ? "防守失败" : "训练超时";
        DefenseNotify($"第 {_defenseStats.Rounds} 轮结束：{label}；击杀 {_defenseController!.DeadCount}/{_defenseBots.Count}；用时 {(Server.TickCount - _defenseRoundStart) * Server.TickInterval:F1} 秒。");
        DefenseNotify($"总计 {_defenseStats.Rounds}，成功 {_defenseStats.Success}，失败 {_defenseStats.Failure}，超时 {_defenseStats.Timeouts}。");
        _defenseController.Cancel(); // stop AI immediately so the finished round cannot keep hurting CT
        _defenseNextRound = Server.TickCount + DefenseTicks(3000); _defenseState = "between";
        foreach (var b in _defenseBots) if (NativeReplayEngine.Player(b.Slot) is { IsValid: true } p && p.Handle == b.Handle && p.UserId == b.UserId) _api?.Lock(b.Slot, BotControllerApi.LockKind.All);
    }
    private void TrackDefenseEntities()
    {
        foreach (var name in _defenseEntityClasses)
            foreach (var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>(name))
            {
                uint serial = entity.EntityHandle.Raw;
                if (_defenseEntityBaseline.Contains(serial) || _defenseTemporary.ContainsKey(serial)) continue;
                var ownerPawn = entity.OwnerEntity.Value;
                var owner = ownerPawn is { IsValid: true, DesignerName: "player" } ? ownerPawn.As<CCSPlayerPawn>().Controller.Value : null;
                if (owner == null && name.EndsWith("_projectile", StringComparison.Ordinal)) owner = entity.As<CBaseGrenade>().Thrower.Value?.Controller.Value;
                if (owner == null && ownerPawn is { IsValid: true } projectile && projectile.DesignerName.EndsWith("_projectile", StringComparison.Ordinal))
                    owner = projectile.As<CBaseGrenade>().Thrower.Value?.Controller.Value;
                // Never delete unrelated entities: require the owner to be one of this round's participants.
                if (owner != null && _defenseParticipantHandles.Contains(owner.EntityHandle.Raw)) _defenseTemporary[serial] = name;
            }
    }
    private void CleanupDefenseEntities()
    {
        foreach (var (handle, name) in _defenseTemporary)
        {
            var entity = new CHandle<CBaseEntity>(handle).Value;
            if (entity is not { IsValid: true } || entity.DesignerName != name) continue;
            if (name.StartsWith("weapon_", StringComparison.Ordinal) && entity.OwnerEntity.Value is { IsValid: true, DesignerName: "player" } pawn &&
                pawn.As<CCSPlayerPawn>().Controller.Value is { } holder && !_defenseParticipantHandles.Contains(holder.EntityHandle.Raw)) continue;
            entity.Remove();
        }
        _defenseTemporary.Clear(); _defenseEntityBaseline.Clear();
    }
    private void CleanupDefenseBots()
    {
        if (_defenseBots.Count == 0) return;
        _defenseController?.Cancel();
        var owned = _defenseBots.Where(b => b.Slot >= 0).ToList();
        if (_defenseState == "preparing")
            foreach (var bot in Utilities.GetPlayers().Where(p => p.IsValid && p.IsBot && p.TeamNum == 2 && p.UserId is int id && !_defenseSpawnBaseline.Contains(id)).Take(_defenseBots.Count(b => b.Slot < 0)))
                if (!owned.Any(b => b.Handle == bot.Handle)) owned.Add((0, bot.Slot, bot.Handle, bot.UserId!.Value));
        int otherBots = Utilities.GetPlayers().Count(p => p.IsValid && p.IsBot && !owned.Any(b => b.Handle == p.Handle));
        Server.ExecuteCommand("bot_quota " + otherBots);
        foreach (var b in owned)
        {
            var bot = NativeReplayEngine.Player(b.Slot);
            if (bot is not { IsValid: true, IsBot: true } || bot.Handle != b.Handle || bot.UserId != b.UserId) continue;
            try { _defenseEngine?.Stop(b.Slot); _api?.Unlock(b.Slot, BotControllerApi.LockKind.All); }
            catch (Exception e) { _defenseError = "BOT 解锁清理失败：" + e.Message; DefenseNotify(_defenseError); }
            if (bot.UserId is int id) Server.ExecuteCommand("kickid " + id);
        }
        _defenseBots.Clear();
    }
    private void StopDefense(string reason)
    {
        bool wasActive = DefenseBusy;
        bool clean = true;
        _defenseGeneration++; _defenseLoading = false;
        if (_defenseCvars != null)
        {
            try { TrackDefenseEntities(); } catch (Exception e) { clean = false; _defenseError = "实体追踪清理警告：" + e.Message; DefenseNotify(_defenseError); }
            try { CleanupDefenseBots(); } catch (Exception e) { clean = false; _defenseError = "BOT 清理警告：" + e.Message; DefenseNotify(_defenseError); }
            try { CleanupDefenseEntities(); } catch (Exception e) { clean = false; _defenseError = "临时实体清理警告：" + e.Message; DefenseNotify(_defenseError); }
        }
        _defenseState = "idle";
        if (_defenseCvars != null)
        {
            foreach (var (key, value) in _defenseCvars) Server.ExecuteCommand(key + " " + value);
            _defenseCvars = null;
            string file = Path.Combine(_dataRoot, "defense-recovery.json"); if (File.Exists(file)) File.Delete(file);
        }
        if (wasActive) DefenseNotify(reason + (clean ? "；已清理 Defense BOT 并恢复训练参数。" : "；部分清理失败，请检查错误日志。"));
    }
    private void RecoverDefenseEnvironment()
    {
        string file = Path.Combine(_dataRoot, "defense-recovery.json"); if (!File.Exists(file)) return;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? throw new InvalidDataException("Defense 恢复文件无效。");
        string[] allowed = ["bot_dont_shoot", "bot_difficulty", "bot_stop", "bot_zombie", "bot_quota", "bot_quota_mode", "mp_respawn_immunitytime"];
        if (values.Keys.Any(k => !allowed.Contains(k)) || values.Values.Any(v => !System.Text.RegularExpressions.Regex.IsMatch(v, "^[a-zA-Z0-9_.+-]{1,32}$"))) throw new InvalidDataException("Defense 恢复文件不安全。");
        foreach (var (key, value) in values) Server.ExecuteCommand(key + " " + value);
        File.Delete(file);
    }
}
