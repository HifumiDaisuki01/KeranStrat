# KeranStrat Defense

Defense 让 1～5 名真人 CT 协同对抗 1～5 名已有 T 方战术 BOT：BOT 先执行原生录制，再按各自的交接时间恢复 CS2 战斗 AI。双方人数无需一致。与原版录制系统共用服务器，使用独立的 `!ksd` 指令和白名单。

## 配置权限

在现有 `configs/plugins/KeranStrat/KeranStrat.json` 中合并：

```json
"DefenseEnabled": true,
"DefenseAllowedSteamIDs": ["你的SteamID64字符串"],
"DefenseRoundSeconds": 120
```

这里的占位文字必须替换为真实 SteamID64。发行模板的 Defense 白名单为空；不会继承 `AllowedSteamIDs`。原版 `Enabled` 和 `AllowedSteamIDs` 继续单独控制 `!ks`。两个功能均使用服务器认证的 SteamID，不依赖管理员权限插件。

配置文件自动热重载；房主也可 `!ksd reload`。关闭 Defense 或房主失去权限会解散会话、恢复临时参数，保留 Practice。普通成员失去权限则移出队伍。禁用原版不会自动禁用 Defense。

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

## 与朋友协同防守

成功 `load` 的玩家建立会话并自动成为首位 CT，房主身份保持到会话解散。朋友需要独立的 Defense 白名单权限，在 CT 队购买自己的装备，走到自己的防守点后执行：

```text
!ksd join
!ksd position
```

房主执行 `!ksd team` 查看最多五人的准备状态；所有人保存位置后再 `!ksd start`。加入和修改位置只允许在训练停止时进行，历史保存过位置不代表已经加入。

房主管理战术、难度、交接、自动循环、开始／停止及切图。队员只能管理自己的加入、退出、位置，并查询队伍和状态。交火或准备时 `leave` 延迟到本轮结束后退出；轮间退出立即生效。继续自动训练时，离队成员转为观察者，避免干扰交火。房主不能直接 leave：用 `stop` 保留队伍，或 `close` 解散。

开始前如有未加入的真人处于 T/CT，会提示其转入观察者。训练过程中出现非成员参战则停止本轮且不计成绩。普通成员断线或离开 CT 后移除，其余 CT 继续；房主断线、换队或失去权限会安全解散，不自动转移房主。断线重连后必须重新 join。

## 每轮流程

1. 固定本轮 CT 名单，复活全部成员，等待所有 Pawn 就绪，再分别恢复各自位置、视角、100 血、护甲、武器和弹药。
2. 只生成有效轨迹对应的 T BOT，支持 1～5 人及非连续编号。
3. BOT 预加载后倒计时三秒，以统一服务器 Tick 按偏移启动。
4. 每个 BOT 独立停止回放、解除锁定、进入原生战斗 AI；实体、位置和当前装备保留。
5. 全部 T 被消灭且至少一名 CT 存活为成功；全部有效 CT 死亡为失败；同次状态检查双方全灭为平局；其余超过时限单独记为超时。
6. 默认等待三秒后自动清理并重置下一轮，不重新加载地图。

开始时分别捕获存活 CT 当前配装，保留各自 `position` 设置的位置；已死亡成员使用保存的配装。后续循环恢复这一配装。改变防守点时房主先停止，再由本人执行 `position`。改变武器后重新 `start` 即可更新配装。

一人阵亡不会结束团队防守；死亡状态锁定到本轮结束，意外自动复活不能再次参战。全体就绪超时会报告具体成员并中止，不无限等待或重复生成 BOT。掉线不记作击杀；没有有效 CT 时停止且不计战败。

一名 BOT 死亡只结束自己的行为，不中断其他 BOT；意外断线、缓冲加载失败或交接失败会安全停止整个训练并报告错误。

进攻 BOT 的购买资金保持为零，避免在全图购买的 Practice 环境中交接 AI 后自行买枪、替换录制配装；真人购买及资金补给保持正常。

## 指令

| 指令 | 功能 |
|---|---|
| `!ksd` | 帮助 |
| `!ksd practice` | 进入或确认训练环境 |
| `!ksd exit` | 停止并恢复 Dust2 死斗 |
| `!ksd close` | 房主解散会话，保留 Practice |
| `!ksd join` | 训练停止时加入当前 CT 队伍 |
| `!ksd leave` | 退出；交火中延迟到本轮结束 |
| `!ksd team` | 队伍成员、房主与准备状态 |
| `!ksd map <地图>` | 切换已安装的 de_ 训练地图 |
| `!ksd list` | 列出当前地图的 T 方战术 |
| `!ksd load <名称或ID>` | 只读加载已有 T 方战术 |
| `!ksd position` | 已加入成员保存自己的 CT 位置、视角和装备 |
| `!ksd start` | 开始防守 |
| `!ksd stop` | 房主停止训练，保留队伍和 Practice |
| `!ksd status` | 状态、交接时间和成绩 |
| `!ksd repeat on/off` | 自动循环开关，可在训练中切换 |
| `!ksd difficulty <1-4>` | 简单、普通、困难、专家；开始前设置 |
| `!ksd handoff <1-5> <毫秒或auto>` | 单队员局部交接时间 |
| `!ksd reload` | 重载配置 |

设置位置、交接和难度等编辑命令需要先停止训练。Defense 会话持有轻量独占状态，原版录制、回放等世界操作需要房主先 `!ksd close`；普通成员不能通过 `!ks exit/map` 绕过房主。可信服务端控制台保留紧急取消能力。

## 场景与恢复

Defense 不改写原版 `.ks`。场景单独存储在 `plugins/KeranStrat/data/defense/<房主SteamID64>/<战术ID>.json`，包含 `Defenders` 字典（每名成员的 SteamID64 对应完整防守 Track）、难度、交接、自动循环和 `RoundSeconds`。旧单人 `Defender` 保留并映射为房主配置。新场景的时间上限取 `DefenseRoundSeconds`；已有场景可以在自己的 JSON 中调整，范围 10～600 秒。

每场景文件最多 64 KiB，最多保存 32 名历史成员的位置，场景总数最多 1024；当前队伍仍最多五人。只有能确认归属于本轮参与者的投掷物、火焰及装备实体才会进入清理集合；使用实体序列号防止地址复用误删，真人持有或已经被其他玩家拾取的武器予以保留。

Defense 临时修改 BOT 射击、难度、停止／僵尸开关、quota、出生保护和双方自动复活参数，停止、房主掉线、换图和卸载时恢复。`defense-recovery.json` 保存修改前的值，异常重启时恢复。原版 Practice 默认仍禁止 BOT 自主射击；原版 `!ks play` 没有战斗 AI 交接。

## 服务端诊断

以下仅允许可信服务端控制台，且要求空服、无其他 BOT、Dust2 的空闲 Practice；诊断使用出生点附近的受控目标，玩家训练仍支持已安装的 de_ 地图：

- `css_ksd_probe`：原生回放后交接 AI，观察 CT 受控目标的真实伤害。
- `css_ksd_selftest`：交火、1/3/5 独立交接、死亡隔离及失败／成功／超时循环测试。
- `css_ksd_cooptest`：受控 CT 的十轮循环、1/2/3/5 人矩阵、位置及配装恢复、团队结算和实际断线测试。
- `css_ksd_cooptest matrix`：只跑 5 对 5 和断线场景的短诊断，单独写入 `defense-cooptest-matrix-report.json`，不声称完成十轮循环。
- `css_ksd status` / `css_ksd stop`：诊断和紧急停止。

测试结果位于插件 data 中的 `defense-probe-report.json`、`defense-selftest-report.json`、`defense-cooptest-report.json`。可信诊断可使用受控 CT BOT，不开放给游戏内玩家或绕过真人加入的白名单检查。详细验证范围见 [DEFENSE_TEST_REPORT.md](DEFENSE_TEST_REPORT.md)。

## 兼容性

当前配套引擎为 CS2-Bot-Controller 0.7.2 / ABI 22。CS2 更新可能改变签名、Pawn 布局、AI Hook 或 usercmd 结构，升级游戏后先检查 `meta list`、`bc_status` 与空服诊断。不要混用其他版本 NativeApi 与原生二进制。原始作者和 AGPL/GPL 许可证继续保留，详见 [THIRD_PARTY.md](THIRD_PARTY.md)。
