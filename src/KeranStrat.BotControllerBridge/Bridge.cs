using System.Reflection;
using System.Runtime.InteropServices;
using BotControllerApi;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;

namespace KeranStrat.BotControllerBridge;

public sealed class BridgeConfig : BasePluginConfig
{
    public string NativePath { get; set; } = "../../../BotController/bin/linuxsteamrt64/BotController.so";
}

// A restricted capability provider. The upstream unmanaged bindings remain unmodified.
// We deliberately do not register the upstream unrestricted !record/!replay commands.
public sealed class Bridge : BasePlugin, IPluginConfig<BridgeConfig>
{
    public override string ModuleName => "KeranStrat.BotControllerBridge";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "Keran Technology; native API by XBribo and contributors";
    public BridgeConfig Config { get; set; } = new();
    public void OnConfigParsed(BridgeConfig config) => Config = config;
    public override void Load(bool hotReload)
    {
        string path = Path.GetFullPath(Path.Combine(ModuleDirectory, Config.NativePath));
        if (!File.Exists(path)) throw new FileNotFoundException("BotController native dependency is missing.", path);
        NativeLibrary.SetDllImportResolver(typeof(BotController).Assembly,
            (string name, Assembly assembly, DllImportSearchPath? search) => name == "BotController" ? NativeLibrary.Load(path) : nint.Zero);
        if (!BotController.IsCompatible()) throw new InvalidOperationException("BotController native ABI must be 22.");
        Capabilities.RegisterPluginCapability(new PluginCapability<IBotControllerApi>("botcontroller:api"), () => new BotControllerApiImpl());
        CounterStrikeSharp.API.Server.PrintToConsole("[KeranStrat] Restricted bridge ready; ABI=" + BotController.AbiVersion);
    }
}
