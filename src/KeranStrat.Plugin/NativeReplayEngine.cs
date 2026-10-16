using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

internal sealed class NativeReplayEngine(IBotControllerApi api) : IReplayEngine
{
    private readonly Dictionary<int, nint> _owned = [];
    private readonly Dictionary<int, Track> _motions = [];
    internal static CCSPlayerController? Player(int slot) => slot < 0 ? null : Utilities.GetPlayerFromSlot(slot);
    private bool Owns(int slot) => _owned.TryGetValue(slot, out var handle) && Player(slot) is { IsValid: true, IsBot: true } bot && bot.Handle == handle;
    public bool Start(int slot) => Owns(slot) && api.Lock(slot, LockKind.All) && api.StartReplay(slot);
    public void Stop(int slot)
    {
        if (Owns(slot)) { api.StopReplay(slot); api.Unlock(slot, LockKind.All); }
        _owned.Remove(slot);
        _motions.Remove(slot);
    }
    public bool IsPlaying(int slot) => api.IsReplaying(slot);
    public int Cursor(int slot) => api.ReplayCursor(slot);
    public bool IsAlive(int slot) => Owns(slot) && Player(slot) is { PawnIsAlive: true };
    public float? PositionError(int slot, int cursor)
    {
        if (cursor <= 0 || !_motions.TryGetValue(slot, out var track)) return null;
        var origin = Player(slot)?.PlayerPawn.Value?.AbsOrigin;
        if (origin == null) return null;
        var expected = track.Ticks[Math.Min(cursor - 1, track.Ticks.Length - 1)].Post;
        float x = origin.X - expected.OriginX, y = origin.Y - expected.OriginY, z = origin.Z - expected.OriginZ;
        return MathF.Sqrt(x * x + y * y + z * z);
    }

    public void Prepare(CCSPlayerController bot, Track track)
    {
        if (!bot.IsBot || !bot.PawnIsAlive || bot.PlayerPawn.Value == null)
            throw new InvalidOperationException("BOT 尚未出生。");
        _owned[bot.Slot] = bot.Handle;
        _motions[bot.Slot] = track;
        if (!api.SetReplayPawn(bot.Slot, bot.PlayerPawn.Value.Handle) || !api.Lock(bot.Slot, LockKind.All))
            throw new InvalidOperationException("无法绑定或锁定 BOT。");
        Inventory.Restore(bot, track);
        if (!api.LoadReplay(bot.Slot, track.Ticks, track.Subticks, track.Commands))
            throw new InvalidOperationException("原生引擎拒绝轨迹缓冲。");
    }
}

internal static class Inventory
{
    public static Track Capture(CCSPlayerController player, int role)
    {
        var pawn = player.PlayerPawn.Value ?? throw new InvalidOperationException("玩家 Pawn 不存在。");
        var origin = pawn.AbsOrigin ?? throw new InvalidOperationException("玩家位置不可用。");
        var angles = pawn.EyeAngles;
        var items = new List<Equipment>();
        if (pawn.WeaponServices != null)
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon is { IsValid: true } && weapon.DesignerName.StartsWith("weapon_", StringComparison.Ordinal))
                    items.Add(new Equipment(weapon.DesignerName, weapon.Clip1, weapon.ReserveAmmo.Length > 0 ? weapon.ReserveAmmo[0] : -1));
            }
        return new Track { Role = role, Initial = new(origin.X, origin.Y, origin.Z, angles.X, angles.Y, angles.Z),
            Equipment = items.ToArray(), Health = pawn.Health, Armor = pawn.ArmorValue,
            Helmet = pawn.ItemServices != null && new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasHelmet };
    }
    public static void Restore(CCSPlayerController player, Track track)
    {
        if (!player.PawnIsAlive) player.Respawn();
        var pawn = player.PlayerPawn.Value ?? throw new InvalidOperationException("玩家 Pawn 不存在。");
        player.RemoveWeapons();
        foreach (var item in track.Equipment) player.GiveNamedItem(item.ClassName);
        if (pawn.WeaponServices != null)
            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon is not { IsValid: true }) continue;
                var item = track.Equipment.FirstOrDefault(e => e.ClassName == weapon.DesignerName);
                if (item == null) continue;
                if (item.Clip >= 0) weapon.Clip1 = item.Clip;
                if (item.Reserve >= 0 && weapon.ReserveAmmo.Length > 0) weapon.ReserveAmmo[0] = item.Reserve;
            }
        pawn.Health = Math.Clamp(track.Health, 1, 100); pawn.ArmorValue = Math.Clamp(track.Armor, 0, 100);
        if (pawn.ItemServices != null) new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasHelmet = track.Helmet;
        var p = track.Initial;
        pawn.Teleport(new CounterStrikeSharp.API.Modules.Utils.Vector(p.X, p.Y, p.Z),
            new CounterStrikeSharp.API.Modules.Utils.QAngle(p.Pitch, p.Yaw, p.Roll),
            new CounterStrikeSharp.API.Modules.Utils.Vector(0, 0, 0));
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
    }
}
