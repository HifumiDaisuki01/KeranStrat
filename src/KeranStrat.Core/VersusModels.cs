namespace KeranStrat.Core;

public sealed class VersusScene
{
    public string Kind { get; set; } = "KeranStrat.Versus";
    public int Format { get; set; } = 1;
    public int Abi { get; set; } = 22;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public string Creator { get; set; } = "";
    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;
    public double TickInterval { get; set; } = 1d / 64;
    public string GameVersion { get; set; } = "";
    public int TickSize { get; set; } = new Tactic().TickSize;
    public int SubtickSize { get; set; } = new Tactic().SubtickSize;
    public int CommandSize { get; set; } = new Tactic().CommandSize;
    public string Mode { get; set; } = "hybrid";
    public bool Repeat { get; set; }
    public int RoundSeconds { get; set; } = 120;
    public Dictionary<string, Track> Tracks { get; set; } = [];
    public Dictionary<string, int?> Handoff { get; set; } = [];
    public static void ValidateName(string name) => Tactic.ValidateName(name);
    public static string Key(int team, int role) => team is 2 or 3 && role is >= 1 and <= 5
        ? (team == 2 ? "T" : "CT") + role : throw new ArgumentException("阵营或角色编号无效。");
    public static (int Team, int Role) Parse(string key)
    {
        for (int team = 2; team <= 3; team++) for (int role = 1; role <= 5; role++)
            if (key == Key(team, role)) return (team, role);
        throw new InvalidDataException("KSV 角色身份无效。");
    }
    public void Validate(int maxTicks = 11520, bool playable = false)
    {
        if (Kind != "KeranStrat.Versus" || Format != 1 || Abi != 22 || Mode is not ("strict" or "hybrid") ||
            RoundSeconds is < 10 or > 600 || Tracks.Count > 10 || Handoff.Count > 10)
            throw new InvalidDataException("KSV 场景格式或参数无效。");
        foreach (string key in Tracks.Keys) Parse(key);
        for (int team = 2; team <= 3; team++)
        {
            var tracks = Tracks.Where(kv => Parse(kv.Key).Team == team).ToDictionary(kv => Parse(kv.Key).Role, kv => kv.Value);
            if (playable && tracks.Count == 0) throw new InvalidOperationException("双方至少各需一条有效轨迹。");
            new Tactic { Id = Id, Name = Name, Map = Map, Team = team, TickInterval = TickInterval,
                TickSize = TickSize, SubtickSize = SubtickSize, CommandSize = CommandSize, Tracks = tracks }.Validate(maxTicks);
        }
        foreach (var (key, milliseconds) in Handoff)
        {
            if (!Tracks.TryGetValue(key, out var track)) throw new InvalidDataException("交接设置引用不存在的角色。");
            DefenseBotController.HandoffTicks(milliseconds, track, TickInterval);
        }
    }
    public VersusScene Snapshot()
    {
        var result = (VersusScene)MemberwiseClone();
        result.Handoff = new(Handoff);
        result.Tracks = Tracks.ToDictionary(kv => kv.Key, kv => new Tactic { Tracks = new() { [kv.Value.Role] = kv.Value } }.Snapshot().Tracks[kv.Value.Role]);
        return result;
    }
}
