# 安装、升级与构建

## 环境

- Linux x86_64 CS2 Dedicated Server。
- Metamod:Source 与 CounterStrikeSharp（.NET 10）。
- CS2-Bot-Controller 0.7.2，原生 ABI 22；Release 已附配套文件。

## 安装

1. 安排维护窗口，停止服务器，备份即将替换的路径。
2. 解压 Release，将 `addons/` 合并到 `game/csgo/`。不要替换整个现有 addons 目录。
3. 在 `addons/counterstrikesharp/configs/plugins/KeranStrat/KeranStrat.json` 中填写 `AllowedSteamIDs`，每个 SteamID64 使用字符串。
4. 启动服务器。服务器控制台可使用 `meta list`、`bc_status`、`css_plugins list` 和 `css_ks status` 查看加载状态。
5. 白名单玩家在游戏内输入 `!ks practice` 开始训练。

安装包结构：

```text
addons/BotController/
addons/metamod/BotController.vdf
addons/counterstrikesharp/shared/BotControllerApi/
addons/counterstrikesharp/plugins/KeranStrat/
addons/counterstrikesharp/plugins/KeranStrat.BotControllerBridge/
addons/counterstrikesharp/configs/plugins/KeranStrat/
addons/counterstrikesharp/configs/plugins/KeranStrat.BotControllerBridge/
```

## 配置与升级

默认空白名单拒绝所有游戏指令。填写自己的 SteamID64 后，配置会在安全空闲点热重载；也可在可信服务器控制台执行 `css_ks reload`。已有白名单玩家可使用 `!ks reload`。

升级时保留已有配置和 `plugins/KeranStrat/data/`。二进制升级应在维护窗口进行；不要在录制或回放过程中替换程序集。游戏更新后应使用匹配版本的框架与原生依赖。

## 从源码构建

需要 .NET 10 SDK：

```bash
bash scripts/build.sh
# 或使用目标服务器的配套引用
CSS_ROOT=/path/to/game/csgo/addons/counterstrikesharp bash scripts/build.sh
# 也可指定 SDK 路径
DOTNET=/path/to/dotnet bash scripts/build.sh
```

脚本构建桥接层和主插件，然后运行独立检查程序。引用程序集在 `refs/`，上游对应源码与许可证在 `third_party/`。不要混用不同发布版的 NativeApi、API 程序集和原生二进制。

## 卸载

先执行 `!ks exit`，等待恢复死斗并停止服务器。备份战术 data 目录，然后仅移除上方列出的 KeranStrat 相关路径；如果已有共享依赖被其他插件使用，应保留它们。恢复自己备份的同名文件后启动服务器。
