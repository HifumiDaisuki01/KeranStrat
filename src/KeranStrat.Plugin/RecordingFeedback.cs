using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using KeranStrat.Core;

namespace KeranStrat.Plugin;

public sealed partial class KeranStratPlugin
{
    private int _lastRecordedRole;
    private int _hudNextTick;
    private int _hudUntil;
    private string _hudText = "";

    private static string Duration(double seconds)
    {
        seconds = Math.Max(0, seconds);
        return ((int)seconds / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
            (seconds % 60).ToString("00.0", CultureInfo.InvariantCulture);
    }
    private void StopShortcut(CCSPlayerController? player, CommandInfo command)
    {
        // Route aliases through the exact same authentication and ownership checks as !ks stop.
        Command(player, command, "stop");
    }
    private void RecordingHudTick()
    {
        bool active = _state is "recording" or "countdown";
        if ((!active && Server.TickCount >= _hudUntil) || Server.TickCount < _hudNextTick) return;
        var player = NativeReplayEngine.Player(_ownerSlot);
        if (player is not { IsValid: true, IsBot: false } || player.SteamID != _owner) return;
        string text = _hudText;
        if (_state == "countdown" && _recording != null)
            text = $"<font color='#FFD166'>准备录制 {_recording.Role} 号 · {Math.Max(1, (int)Math.Ceiling((_countdownAt - Server.TickCount) * Server.TickInterval))}</font>";
        else if (_state == "recording" && _recording != null)
            text = $"<font color='#FF6666'>● 正在录制 {_recording.Role} 号</font><br>{Duration((Server.TickCount - _recordStart) * Server.TickInterval)} / {Duration(Config.MaxRecordingSeconds)}<br><font color='#AAAAAA'>快捷停止：绑定 mouse3 → css_ks_stop</font>";
        player.PrintToCenterHtml(text);
        _hudNextTick = Server.TickCount + ReplayCoordinator.ToTicks(100, Server.TickInterval);
    }
    private void RecordingFinishedHud(int role, int frames)
    {
        _lastRecordedRole = role;
        _hudText = $"<font color='#80ED99'>已停止录制 {role} 号 · {Duration(frames * Server.TickInterval)}</font>";
        _hudUntil = Server.TickCount + ReplayCoordinator.ToTicks(3000, Server.TickInterval);
        _hudNextTick = 0;
    }
}
