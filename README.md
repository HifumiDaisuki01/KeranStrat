# KeranStrat

**CS2 战术录制与 BOT 同步回放插件 · Keran Technology**

一个人录制整套团队战术，让对应数量的 BOT 在同一条时间轴上执行。支持 1～5 名队员、独立重录、延迟启动与本地战术库，无需客户端 Mod、数据库或网页后台。

## 功能

- **自由编组**：录制 1～5 名队员，也可以只使用 1、3、5 等非连续编号；回放只生成已录制队员的 BOT。
- **原生动作录制**：基于 CS2-Bot-Controller，保存移动、视角、按钮、武器、Subtick 和命令帧数据，通过原生命令路径回放。
- **同步与错峰执行**：统一服务器 Tick 时间轴，支持每名队员单独设置启动偏移，也可以单独回放一个队员。
- **独立编辑**：重录、移除或补录单个队员，保留其他轨迹；支持设置起点、复制战术和重复播放。
- **战术库**：按地图保存、加载、列出、复制和删除战术，使用原子文件写入及容量限制。
- **一键训练**：进入训练环境、补齐武器和投掷物、切换地图；退出时自动保存、清理 BOT，并恢复 Dust2 死斗。
- **SteamID64 白名单**：训练与编辑指令统一鉴权，支持配置热重载，不依赖 CSSharp 管理员权限。
- **轻量部署**：插件本地存储，无数据库、Web 服务或客户端插件依赖。

## KeranStrat Defense

独立的 `!ksd` 防守训练：1～5 名真人 CT 协同对抗 1～5 个 T BOT。进攻方先按已有录制执行爆弹，再独立交接原生战斗 AI。每名 CT 独立保存位置和配装，一人阵亡后队友继续防守，全队共享成功／失败／超时／平局统计并自动重置下一轮。

Defense 使用独立的 `DefenseAllowedSteamIDs` 白名单，不继承原版权限；不会改写战术文件，原版 `!ks play` 继续保持纯轨迹回放。

```text
!ksd practice
!ksd load B_Execute
!ksd position
!ksd difficulty 3
!ksd start
```

朋友同样需要 Defense 白名单权限，在 CT 队执行 `!ksd join`、`!ksd position`。房主用 `!ksd team` 检查准备状态，再启动训练。`!ksd stop` 保留队伍和 Practice；`!ksd close` 解散队伍；`!ksd exit` 恢复死斗。

[Defense 使用与配置](DEFENSE.md) · [Defense 测试记录](DEFENSE_TEST_REPORT.md)

## 安装

运行环境：Linux x86_64、CS2 Dedicated Server、Metamod:Source、CounterStrikeSharp（.NET 10）。配套原生引擎为 CS2-Bot-Controller 0.7.2 / ABI 22。

1. 在 Releases 下载 `KeranStrat-v0.5.5-linux-x64.zip`。
2. 在维护窗口停止服务器，备份将要替换的文件，将压缩包中的 `addons/` 合并到 `game/csgo/addons/`。
3. 编辑 `addons/counterstrikesharp/configs/plugins/KeranStrat/KeranStrat.json`，在 `AllowedSteamIDs` 中填写自己的 SteamID64 字符串。
4. 启动服务器，进服后输入 `!ks` 查看帮助。

默认白名单为空。升级时保留已有配置和插件 `data/` 目录，不要用默认模板覆盖自己的白名单。

详见 [安装与构建](INSTALL.md)、[配置说明](CONFIGURATION.md)。

## 使用手册

访问 [cs.jinhsi.cn/help](https://cs.jinhsi.cn/help)：简洁快速上手、KS / KSV / KSD 完整指令、参数和示例。游戏内输入 `!ks`、`!ksv`、`!ksd` 或各自的 `help` 获取网址，不自动打开浏览器。网页源码在 `web/help/`，由 `scripts/build-help.py` 生成。

## 快速使用

```text
!ks practice
!ks kit
!ks new B区进攻
!ks record 1
!ks stop
!ks record 3
!ks stop
!ks offset 3 1500
!ks save
!ks play
!ks exit
```

进入训练后先选择 T/CT 并出生。`record` 倒计时结束后开始录制，`stop` 结束当前轨迹。上例生成两名 BOT，3 号比 1 号晚 1.5 秒启动。

训练期间可随时随地按 B 购买，资金补至 60000。`!ks kit` 保留已有主武器，补齐护甲与投掷物（含两颗闪光弹）；`!ks kit awp` 可直接切换 AWP 配装。开始录制时保留当前已购买的装备，回放恢复录制开始时的弹药和投掷物数量。

新角色使用当前战术起点；需要调整时，走到目标位置执行 `!ks anchor`。重录已有队员会恢复该队员自己的起点。编辑后使用 `!ks save` 持久保存；切图和退出训练会自动保存。

战术库按地图共享，有原版权限的玩家可用 `!ks load 名称` 加载同一套战术，轮流编辑和保存。`!ks new 名称`、`!ks copy 名称` 会先检查当前地图的已保存战术，重名时拒绝创建并保留当前草稿；已有战术请直接加载编辑。不同地图可使用相同名称。

训练模式切换会提示全服并重新加载地图。服务器平时的 Dust2 死斗启动配置保持不变。

## 录制计时与快捷停止

录制 HUD 仅显示单行时间，保留原字体与阶段颜色：倒计时黄色、录制红色、结束绿色。持续绘制避免间歇闪烁，停止后保留一秒再消失。

在客户端控制台执行一次，即可用鼠标中键停止录制，无须打开聊天框：

```text
bind "mouse3" "css_ks_stop"
```

此绑定会替换原鼠标中键功能，也可以选择其他按键。快捷停止采用与聊天指令相同的 SteamID64 白名单和操作归属检查。

录制结束后，`!ks trim 300` 裁掉最近录制队员的末尾 300 毫秒；`!ks trim 300 2` 指定裁剪 2 号。按服务器 Tick 向上取整，并至少保留一帧。裁剪不会自动保存，确认后使用 `!ks save`。

进入训练会结束热身、关闭开场介绍及半场切换，待引擎确认就绪后再允许录制。

## 指令

帮助指令无需权限；训练及编辑操作需对应模式的 SteamID64 白名单授权。

| 指令 | 功能 |
|---|---|
| `!ks` | 查看帮助 |
| `!ks practice` | 进入训练模式 |
| `!ks exit` | 保存并恢复 Dust2 死斗 |
| `!ks map <地图>` | 切换训练地图，如 de_mirage、de_inferno、de_dust2 |
| `!ks kit [主武器]` | 保留主武器补给；`!ks kit awp` 配 AWP 和双闪 |
| `!ks new <名称>` | 新建战术 |
| `!ks anchor` | 设置当前起点 |
| `!ks record <1-5>` | 录制或重录队员 |
| `!ks stop` / `css_ks_stop` | 结束录制；后者可绑定按键 |
| `!ks play` | 回放全部有效队员 |
| `!ks replay <1-5>` | 单独回放队员 |
| `!ks offset <1-5> <毫秒>` | 设置启动延迟 |
| `!ks remove <1-5>` | 移除队员轨迹 |
| `!ks trim <毫秒> [队员]` | 裁剪轨迹末尾；默认最近录制的队员 |
| `!ks save` | 保存当前战术 |
| `!ks load <名称或ID>` | 加载战术 |
| `!ks list` | 查看战术列表 |
| `!ks copy <新名称>` | 复制当前战术 |
| `!ks delete <名称或ID>` | 删除战术 |
| `!ks reset` | 停止回放并返回起点 |
| `!ks cancel` | 取消当前操作 |
| `!ks status` | 查看状态 |
| `!ks reload` | 重新加载配置 |
| `!ks source` | 获取源代码地址 |

## 源码与构建

使用 .NET 10 SDK：

```bash
bash scripts/build.sh
```

仓库附带配套引用程序集与第三方源码，不需要在线 NuGet 依赖。也可设置 `CSS_ROOT` 使用目标服务器的引用程序集。架构与存储格式见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 开源许可与致谢

KeranStrat 采用 [AGPL-3.0-only](LICENSE)。原生录制引擎来自 [XBribo/CS2-Bot-Controller](https://github.com/XBribo/CS2-Bot-Controller)，服务器插件框架来自 [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp)。第三方作者、许可证及源码保留在 [THIRD_PARTY.md](THIRD_PARTY.md) 和 `third_party/`。


## KeranStrat Versus 与彩色观察

新增独立 `!ksv` 双阵营战术模拟：每队 1～5 个 BOT，共用 Tick 时间轴，支持严格回放与逐角色原生 AI 交接。场景采用独立 .ksv 文件，权限不继承 KS 或 Defense。使用说明见 [KSV.md](KSV.md)。

KS（默认关）和 KSV（默认开）提供独立彩色 X 光，仅限有对应权限的观察者在回放中观看。支持游戏内实时改色和保存，Defense 永久禁用。详见 [XRAY.md](XRAY.md)。
