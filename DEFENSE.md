# KeranStrat Defense

Defense 让真人 CT 对抗已有 T 方战术：BOT 先执行原生录制，再按各自的交接时间恢复 CS2 战斗 AI。与原版录制系统共用服务器，使用独立的 `!ksd` 指令和白名单。

## 配置权限

在现有 `configs/plugins/KeranStrat/KeranStrat.json` 中合并：

```json
"DefenseEnabled": true,
"DefenseAllowedSteamIDs": ["你的SteamID64字符串"],
"DefenseRoundSeconds": 120
```

这里的占位文字必须替换为真实 SteamID64。发行模板的 Defense 白名单为空；不会继承 `AllowedSteamIDs`。原版 `Enabled` 和 `AllowedSteamIDs` 继续单独控制 `!ks`。两个功能均使用服务器认证的 SteamID，不依赖管理员权限插件。

配置文件自动热重载；Defense 成员也可 `!ksd reload`。关闭 Defense 或移除当前训练玩家的权限会停止训练、恢复临时参数，保留 Practice。禁用原版不会自动禁用 Defense。

## 从录制到防守

先用有原版权限的账号，在 T 队录制并保存进攻战术：

```text
!ks practice
!ks new B_Execute
!ks record 1
!ks stop
!ks record 3
!ks stop
!ks record 5
!ks stop
!ks save
```

接着选择 CT 队，在防守位置配好武器和投掷物：

```text
!ksd practice
!ksd list
!ksd load B_Execute
!ksd position
!ksd difficulty 3
!ksd handoff 1 3000
!ksd handoff 3 5000
!ksd handoff 5 auto
!ksd start
```

交接时间不能超过对应队员轨迹长度；上面数值仅为示例。`auto` 表示该轨迹自然结束。所有时间均从该 BOT 自己的轨迹起点计算，原有 `OffsetMs` 仍生效。

已经处于 Practice 时，`!ksd practice` 直接提示就绪，不重复加载地图。只拥有 Defense 权限的玩家可以独立完成整个防守流程，无须使用 `!ks`。

## 每轮流程

1. 恢复 CT 防守位置、视角、100 血、护甲、武器和弹药。
2. 只生成有效轨迹对应的 T BOT，支持 1～5 人及非连续编号。
3. BOT 预加载后倒计时三秒，以统一服务器 Tick 按偏移启动。
4. 每个 BOT 独立停止回放、解除锁定、进入原生战斗 AI；实体、位置和当前装备保留。
5. 全部 T 被消灭为成功；CT 死亡为失败；超过场景上限单独记为超时。
6. 默认等待三秒后自动清理并重置下一轮，不重新加载地图。

开始时重新捕获 CT 当前配装，但保留 `position` 设置的位置；后续循环始终恢复这一配装。改变防守点时先停止，再执行 `position`。改变武器后重新 `start` 即可更新配装。

一名 BOT 死亡只结束自己的行为，不中断其他 BOT；意外断线、缓冲加载失败或交接失败会安全停止整个训练并报告错误。

## 指令

| 指令 | 功能 |
|---|---|
| `!ksd` | 帮助 |
| `!ksd practice` | 进入或确认训练环境 |
| `!ksd exit` | 停止并恢复 Dust2 死斗 |
| `!ksd map <地图>` | 切换已安装的 de_ 训练地图 |
| `!ksd list` | 列出当前地图的 T 方战术 |
| `!ksd load <名称或ID>` | 只读加载已有 T 方战术 |
| `!ksd position` | 保存 CT 防守位置、视角和装备 |
| `!ksd start` | 开始防守 |
| `!ksd stop` | 停止训练，留在 Practice |
| `!ksd status` | 状态、交接时间和成绩 |
| `!ksd repeat on/off` | 自动循环开关，可在训练中切换 |
| `!ksd difficulty <1-4>` | 简单、普通、困难、专家；开始前设置 |
| `!ksd handoff <1-5> <毫秒或auto>` | 单队员局部交接时间 |
| `!ksd reload` | 重载配置 |

设置位置、交接和难度等编辑命令需要先停止训练。原版操作与 Defense 训练共用轻量独占检查，不能同时录制、播放或争夺切图控制权。

## 场景与恢复

Defense 不改写原版 `.ks`。场景单独存储在 `plugins/KeranStrat/data/defense/<SteamID64>/<战术ID>.json`，包含防守点、配装、难度、交接、自动循环和 `RoundSeconds`。新场景的时间上限取 `DefenseRoundSeconds`；已有场景可以在自己的 JSON 中调整，范围 10～600 秒。

每场景文件最多 64 KiB，场景总数最多 1024。只有能确认归属于本轮参与者的投掷物、火焰及装备实体才会进入清理集合；使用实体序列号防止地址复用误删，已经被其他玩家拾取的武器予以保留。

Defense 临时修改 BOT 射击、难度、停止／僵尸开关、quota 和出生保护参数，停止、掉线、换图和卸载时恢复。`defense-recovery.json` 保存修改前的值，异常重启时恢复。原版 Practice 默认仍禁止 BOT 自主射击；原版 `!ks play` 没有战斗 AI 交接。

## 服务端诊断

以下仅允许可信服务端控制台，且要求空服、无其他 BOT、Dust2 的空闲 Practice；诊断使用出生点附近的受控目标，玩家训练仍支持已安装的 de_ 地图：

- `css_ksd_probe`：原生回放后交接 AI，观察 CT 受控目标的真实伤害。
- `css_ksd_selftest`：交火、1/3/5 独立交接、死亡隔离及失败／成功／超时循环测试。
- `css_ksd status` / `css_ksd stop`：诊断和紧急停止。

测试结果位于插件 data 中的 `defense-probe-report.json`、`defense-selftest-report.json`。详细验证范围见 [DEFENSE_TEST_REPORT.md](DEFENSE_TEST_REPORT.md)。

## 兼容性

当前配套引擎为 CS2-Bot-Controller 0.7.2 / ABI 22。CS2 更新可能改变签名、Pawn 布局、AI Hook 或 usercmd 结构，升级游戏后先检查 `meta list`、`bc_status` 与空服诊断。不要混用其他版本 NativeApi 与原生二进制。原始作者和 AGPL/GPL 许可证继续保留，详见 [THIRD_PARTY.md](THIRD_PARTY.md)。
