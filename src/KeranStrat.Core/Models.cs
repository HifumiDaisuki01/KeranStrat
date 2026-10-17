using BotControllerApi;
using System.Runtime.InteropServices;

namespace KeranStrat.Core;

public sealed record Equipment(string ClassName, int Clip = -1, int Reserve = -1);
public sealed record Pose(float X, float Y, float Z, float Pitch, float Yaw, float Roll);

public sealed class Track
{
    public int Role { get; set; }
    public int OffsetMs { get; set; }
    public Pose Initial { get; set; } = new(0, 0, 0, 0, 0, 0);
    public Equipment[] Equipment { get; set; } = [];
    // Grenade stacks live in player ammo, not in the number of weapon entities.
    // Empty for older archives, which retain the engine's give-item defaults.
    public ushort[] Ammo { get; set; } = [];
    public int Health { get; set; } = 100;
    public int Armor { get; set; }
    public bool Helmet { get; set; }
    public ReplayTick[] Ticks { get; set; } = [];
    public SubtickMove[] Subticks { get; set; } = [];
    public ReplayCommandFrame[] Commands { get; set; } = [];
}

public sealed class Tactic
{
    public int Format { get; set; } = 1;
    public int Abi { get; set; } = 22;
    public int TickSize { get; set; } = Marshal.SizeOf<ReplayTick>();
    public int SubtickSize { get; set; } = Marshal.SizeOf<SubtickMove>();
    public int CommandSize { get; set; } = Marshal.SizeOf<ReplayCommandFrame>();
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public string Creator { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public int Team { get; set; } = 2;
    public double TickInterval { get; set; } = 1.0 / 64;
    public string GameVersion { get; set; } = "";
    public string NativeVersion { get; set; } = "BotController 0.7.2 / ABI 22";
    public Dictionary<int, Track> Tracks { get; set; } = [];

    // Frame arrays are immutable after capture; copy editable metadata before background I/O.
    public Tactic Snapshot() => new()
    {
        Format = Format, Abi = Abi, TickSize = TickSize, SubtickSize = SubtickSize, CommandSize = CommandSize,
        Id = Id, Name = Name, Map = Map, Creator = Creator, Created = Created, Team = Team,
        TickInterval = TickInterval, GameVersion = GameVersion, NativeVersion = NativeVersion,
        Tracks = Tracks.ToDictionary(kv => kv.Key, kv => new Track
        {
            Role = kv.Value.Role, OffsetMs = kv.Value.OffsetMs, Initial = kv.Value.Initial,
            Equipment = kv.Value.Equipment, Ammo = kv.Value.Ammo, Health = kv.Value.Health, Armor = kv.Value.Armor, Helmet = kv.Value.Helmet,
            Ticks = kv.Value.Ticks, Subticks = kv.Value.Subticks, Commands = kv.Value.Commands
        })
    };

    public void Validate(int maxTicks = 11520)
    {
        if (Format != 1 || Abi != 22 || TickSize != Marshal.SizeOf<ReplayTick>() ||
            SubtickSize != Marshal.SizeOf<SubtickMove>() || CommandSize != Marshal.SizeOf<ReplayCommandFrame>())
            throw new InvalidDataException("轨迹格式或 BotController ABI/结构体大小不兼容。");
        ValidateName(Name);
        if (string.IsNullOrWhiteSpace(Map) || Map.Length > 256 || Map.Any(char.IsControl) ||
            Id == Guid.Empty || Team is not (2 or 3) || !double.IsFinite(TickInterval) ||
            TickInterval < 0.001 || TickInterval > 0.1 || Tracks.Count > 5)
            throw new InvalidDataException("战术元数据无效。");
        foreach (var (role, t) in Tracks)
        {
            if (role is < 1 or > 5 || t.Role != role || t.OffsetMs is < 0 or > 120000 ||
                t.Ticks.Length is < 1 || t.Ticks.Length > maxTicks || t.Commands.Length != t.Ticks.Length ||
                t.Subticks.Length > maxTicks * 32 || t.Equipment.Length > 24 || t.Ammo.Length > 256)
                throw new InvalidDataException($"队员 {role} 的轨迹数量或参数无效。");
            if (!Finite(t.Initial.X, t.Initial.Y, t.Initial.Z, t.Initial.Pitch, t.Initial.Yaw, t.Initial.Roll) ||
                t.Equipment.Any(e => !System.Text.RegularExpressions.Regex.IsMatch(e.ClassName, "^weapon_[a-z0-9_]{1,48}$")))
                throw new InvalidDataException("装备或初始位置无效。");
            long subCount = 0;
            foreach (var tick in t.Ticks)
            {
                subCount += tick.NumSubtick;
                if (!Finite(tick.Pre.OriginX, tick.Pre.OriginY, tick.Pre.OriginZ, tick.Pre.Pitch, tick.Pre.Yaw))
                    throw new InvalidDataException("轨迹包含非有限坐标。");
            }
            if (subCount != t.Subticks.Length) throw new InvalidDataException("Subtick 数据不完整。");
        }
    }

    private static bool Finite(params float[] values) => values.All(float.IsFinite);
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name != name.Trim() ||
            name.Any(c => char.IsControl(c) || c is '/' or '\\') || name is "." or "..")
            throw new ArgumentException("名称必须为 1–64 个字符，不能含路径分隔符或控制字符。");
    }
}

public interface ITacticStore
{
    void Save(Tactic tactic);
    Tactic Load(string map, string nameOrId);
    IReadOnlyList<Tactic> List(string map);
    void Delete(string map, string nameOrId);
}
