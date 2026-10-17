# 架构

- `KeranStrat.Core`：战术和五轨迹数据模型、文件存储、统一 ReplayCoordinator。无 CS2 主线程 API，便于独立测试。
- `KeranStrat.Plugin`：统一 SteamID64 白名单、单操作状态机、BOT 生命周期、装备恢复、训练/死斗切换、练习配置快照、异步文件任务与主线程结果队列。
- `KeranStrat.BotControllerBridge`：受限能力提供者，复用上游未经修改的 NativeApi 和 ApiImpl；不注册其公开的 `!record` / `!replay` 指令。
- `BotController`：上游原生 Metamod 引擎。能力契约经共享 BotControllerApi.dll 注册；严格要求 ABI 22。

## 状态和线程

基础状态为 idle → countdown → recording → idle，或 idle → preparing → playing → idle。文件操作另用 busy 保护；取消增加 generation，过期后台结果不能覆盖新会话。Native API 只在游戏主线程调用。存盘前复制可变元数据，已捕获帧数组保持不可变；磁盘序列化在 Task.Run 执行。

所有 BOT 的轨迹在播放前预加载、绑定 Pawn、恢复装备并锁定 AI。一个 OnTick 回调驱动一个 ReplayCoordinator；统一 startTick，每角色 offset 由 ceil(ms / TickInterval) 换算；到期时在同一个主线程回调启动。记录实际启动 Tick 和原生命令游标偏差，不能将调度同时启动等同于所有引擎状态完全一致。

仅生成实际存在的 1～5 条轨迹对应的 BOT；支持不连续角色编号。清理使用服务器 UserId（kickid），不依赖更改后的显示名。采样实际 Pawn 位置与原生游标对应上一帧 Post 位置的距离，报告最大值和采样数；该诊断不等同于客户端投掷物落点误差。

训练模式只在 Deathmatch/Practice 两种稳定状态间转换，中间切图状态拒绝重复操作。调用引擎 ConVar setter 切换 0/1 或 1/2 后执行 map，并在地图开始及若干 Tick 后验证实际值；新地图应用临时训练参数，退出时自动保存并恢复 Dust2 死斗。默认启动参数保持不变。授权不依赖 CSSharp 管理员，只读取 AuthorizedSteamID，空白名单默认拒绝。

## 存储

每战术一个原子提交的 `.ks` ZIP：metadata.json 与每队员的 tracks/N.bin。二进制保留上游 ReplayTick/SubtickMove/ReplayCommandFrame 原始结构体字段，不进行有损的坐标简化。当前格式 1、ABI 22；记录三个结构体大小、TickInterval、游戏版本。跨游戏版本或 Tick 间隔默认拒绝加载。

先写同目录临时文件、Flush(true)，完成后原子替换。读取检查数量、展开长度、结构体尺寸、队员编号、Subtick 对齐和命令帧数量；文件名使用 UUID，地图目录使用地图名称哈希，外部查询不拼接成路径。默认每段最长 180 秒，每地图最多 100 战术，单战术展开最多 64 MiB，总归档配额 256 MiB，剩余空间不足 2 GiB 拒绝保存。

## 恢复

练习参数在修改前写入 practice-recovery.json；正常退出恢复并移除。异常退出时，下次加载恢复快照。只创建和清理本插件拥有的 BOT，不卸载现有插件、不更改正常 cfg。已开始的文件写入可能在取消后完成，但旧回调不会污染新会话。

后续 Web 可通过 ITacticStore 数据访问接口接入；当前不开放远程写入 API、不暴露 RCON。

## Defense 模块

- Core/Defense.cs：独立 DefenseBotController、每 BOT Waiting/Replaying/CombatAI/Dead 状态、本地交接时间校验、成绩与场景模型。
- DefenseCommands.cs：独立 css_ksd 入口、DefenseEnabled/DefenseAllowedSteamIDs 统一鉴权、只读异步加载、每玩家场景 JSON 持久化。
- DefenseManager.cs：Defense 回合生命周期、CT 重置、T BOT 身份绑定、三秒倒计时、判定、循环、临时 ConVar 快照和实体清理。
- DefenseProbe.cs / DefenseSelfTest.cs：仅空服控制台使用的原生交火与循环验证。

原版 _state/_busy/切图标志和 DefenseBusy 构成轻量互斥；Defense 加载或训练期间拒绝原版世界操作，原版操作期间拒绝 Defense。双方共享 PracticeEnvironment 与模式底层，各自保存战术/状态，普通原版命令不会获得 Defense 权限。显式退出和全局地图卸载负责共同停止。

NativeReplayEngine.HandoffToCombatAI 顺序为 StopReplay（清除回放状态和 Pawn 注入绑定）→ Unlock All/Aim/Weapon → 核对状态；不移除实体所有权。上游 CCSBot::Update/Upkeep/UpdateLookAngles 在回放或相应锁存在时跳过原生更新，释放后恢复。最终回合清理才 Stop 并按控制器身份和 UserId 踢出 BOT。

DefenseBotController 使用独立策略：死亡是正常事件；控制器断线、加载失败和提前终止为故障；交接不结束本轮，所有 BOT 消灭、CT 死亡或超时才结束。本地交接 Tick 与每角色 OffsetTicks 分开，所有启动和交接由一个 OnTick 推进。原版 ReplayCoordinator 无交接行为。

临时实体每200ms追踪，使用带序列号的实体句柄及参与者归属，防止重用指针误删。场景文件与原 .ks 分离；代际标记阻止过期后台加载覆盖换图/取消后的状态。Defense 的参数恢复先于 Practice 恢复，避免异常重启后把练习值当作最终正常值。