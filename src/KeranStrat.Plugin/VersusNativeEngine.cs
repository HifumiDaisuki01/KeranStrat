using BotControllerApi;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

internal sealed class VersusNativeEngine(IBotControllerApi api) : IVersusReplayEngine
{
    public NativeReplayEngine Native { get; } = new(api);
    public bool Start(int slot) => Native.Start(slot);
    public void Stop(int slot) => Native.Stop(slot);
    public bool IsConnected(int slot) => Native.IsConnected(slot);
    public bool IsAlive(int slot) => Native.IsAlive(slot);
    public bool IsPlaying(int slot) => Native.IsPlaying(slot);
    public int Cursor(int slot) => Native.Cursor(slot);
    public float? PositionError(int slot, int cursor) => Native.PositionError(slot, cursor);
    public bool HandoffToCombatAI(int slot) => Native.HandoffToCombatAI(slot);
    public bool Hold(int slot) => Native.IsConnected(slot) && api.StopReplay(slot) && api.Lock(slot, LockKind.All);
}
