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
    private bool _hudVisible;

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
        if (!active && Server.TickCount >= _hudUntil) { ClearRecordingHud(); return; }
        var player = NativeReplayEngine.Player(_ownerSlot);
        if (player is not { IsValid: true, IsBot: false } || player.SteamID != _owner) return;
        if (Server.TickCount >= _hudNextTick)
        {
            if (_state == "countdown")
                _hudText = Duration((_countdownAt - Server.TickCount) * Server.TickInterval);
            else if (_state == "recording")
                _hudText = Duration((Server.TickCount - _recordStart) * Server.TickInterval);
            _hudNextTick = Server.TickCount + ReplayCoordinator.ToTicks(100, Server.TickInterval);
        }
        // The game's HTML panel needs continuous drawing, as in CSS's CenterHtmlMenu.
        // Cache the time at 10 Hz, but keep the panel alive every tick to avoid fade/re-entry flashes.
        string color = _state == "countdown" ? "#FFD166" : _state == "recording" ? "#FF6666" : "#80ED99";
        player.PrintToCenterHtml($"<font color='{color}'>{_hudText}</font>", 1);
        _hudVisible = true;
    }
    private void RecordingFinishedHud(int role, int frames)
    {
        _lastRecordedRole = role;
        _hudText = Duration(frames * Server.TickInterval);
        _hudUntil = Server.TickCount + ReplayCoordinator.ToTicks(1000, Server.TickInterval);
        _hudNextTick = 0;
    }
    private void ClearRecordingHud()
    {
        if (!_hudVisible) return;
        _hudVisible = false;
        var player = NativeReplayEngine.Player(_ownerSlot);
        if (player is { IsValid: true, IsBot: false } && player.SteamID == _owner)
            player.PrintToCenterHtml(" ", 1);
    }
}
