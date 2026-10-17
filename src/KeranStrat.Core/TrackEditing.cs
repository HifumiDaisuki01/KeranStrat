namespace KeranStrat.Core;

public static class TrackEditing
{
    // Copy all three native streams at the same tick boundary. Never mutate a saved/in-flight snapshot.
    public static Track TrimEnd(Track track, int milliseconds, double tickInterval)
    {
        if (milliseconds is < 1 or > 120000 || !double.IsFinite(tickInterval) || tickInterval <= 0)
            throw new ArgumentException("裁剪时长必须为 1–120000 毫秒。");
        int removed = ReplayCoordinator.ToTicks(milliseconds, tickInterval);
        int keep = track.Ticks.Length - removed;
        if (keep < 1) throw new ArgumentException("裁剪后必须至少保留一帧，原轨迹未修改。");
        if (track.Commands.Length != track.Ticks.Length) throw new InvalidDataException("命令帧与轨迹不匹配。");
        long total = 0, prefix = 0;
        for (int i = 0; i < track.Ticks.Length; i++)
        {
            total += track.Ticks[i].NumSubtick;
            if (i < keep) prefix = total;
        }
        if (total != track.Subticks.Length) throw new InvalidDataException("Subtick 数据不完整。");
        return new Track
        {
            Role = track.Role, OffsetMs = track.OffsetMs, Initial = track.Initial, Equipment = track.Equipment,
            Ammo = track.Ammo, Health = track.Health, Armor = track.Armor, Helmet = track.Helmet,
            Ticks = track.Ticks[..keep], Commands = track.Commands[..keep], Subticks = track.Subticks[..checked((int)prefix)]
        };
    }
}
