# Versus 与彩色观察验证（0.5.1）

0.5.1 修复 KSV 轮间保持锁定后清理 BOT 的显式解锁。基于 0.5.0 的完整回归，补丁重新执行双阵营七场景、Strict 真实射击和循环测试，新增 NativeLocksReleased=true 的原生锁释放断言；KS/Defense 未修改，沿用下述 0.5.0 同轮回归记录。补丁再次构建零警告零错误，45 项独立检查通过。

补丁报告：2026-10-17T16:28:22Z，Hybrid 原生枪伤 2021、Strict 枪伤 111、真实击杀 16、27 对轮廓实体创建及清理；NativeLocksReleased=true。汇总 /root/keranstrat-work/v051-verification.json。已有 8 份数据及所有既有配置字段仍保持不变，最终恢复 Deathmatch。

## 0.5.0 完整功能与回归基线

基线 main：5b3cedd8f985f7bf89397627eaa3f00dc2f0b603。测试时间：2026-10-17 UTC。目标为 Debian 12 x86_64、CS2 1.41.9.1 / ServerVersion 2000933、CounterStrikeSharp 1.0.376 / .NET 10、原生 BotController 0.7.2 / ABI 22。

## 构建与独立检查

最终候选构建零警告、零错误，45 项独立检查全部通过。新增检查覆盖：十角色原生缓冲逐字节往返、KS/KSV 文件集合隔离、路径及重名拒绝、角色身份与格式/ABI/容量校验、双阵营统一起点、偏移与局部交接、Strict 结束保持锁定且不交接 AI、死亡与断线区分、配色格式与模式隔离、Defense 全组合拒绝可见性、原子配置合并且保留白名单及未知字段。

可执行：`dotnet run --project tests -c Release`。

## 实际 KSV 服务端诊断

已执行可信空服控制台 `css_ksv_selftest`，结果 PASS_VERSUS_SERVER_AUTOMATION，报告时间 2026-10-17T16:18:35Z。

| 场景 | 模式 | 实际结果 |
|---|---|---|
| 1 T / 1 CT | Hybrid | T 胜 |
| 3 T / 1 CT | Hybrid | T 胜 |
| 5 T / 2 CT | Hybrid | T 胜 |
| 5 T / 5 CT | Hybrid | CT 胜 |
| 1 T / 1 CT | Strict | T 胜，未发生 AI 交接 |
| 1 T / 1 CT 连续两轮 | Hybrid / repeat | 两轮 T 胜，自动重建 |

所有回放角色使用同一开始 Tick，角色偏移为 0/8/16/24/32 Tick；auto 等待原生结束后交接，记录为各自实际起点加 65 Tick。1v1 的双方统一从 1267 Tick 开始，均在 1332 Tick 交接；Strict 场景双方从 3483 Tick 开始，HandedOff 均为空。

累计结果：

- NativeAIGunDamage = 2385，仅统计回放后进入 CombatAI 阶段的本场景 BOT 对敌方 BOT 的真实枪伤。
- StrictReplayGunDamage = 111，仅严格回放录制输入，未在播放阶段补注入攻击。
- ActualKillEvents = 18，来自实际敌方 BOT player_death 攻击者归属。
- 创建并清理 27 对原生 Glow／Relay 实体，检查 GlowType 和角色颜色；最多同时十名战斗 BOT。
- 各人数场景经过独立 .ksv 保存和读取；诊断目录结束后移除，未使用原版 .ks 作为 KSV 输入。

诊断先原生录制双方的静止轨迹；Strict 射击夹具来自 T BOT 自主开火的真实命令记录。录制阶段的万血目标仅防止录制搭档提前死亡，不存在于玩家训练或实际场景播放中。原生命令采集早于 InjectUsercmd，因此未用播放时注入攻击来冒充严格回放或原生 AI。

## KS / Defense 实际回归与恢复

同一最终候选依次执行并通过：

- `css_ksd_selftest`：原生枪击 44 点实际伤害、T BOT 真实击杀 CT；跳号角色 1/3/5、独立偏移及交接、死亡隔离、三轮成功/失败/超时和参数恢复。
- `css_ks selftest`：原生录制 96 帧、裁剪至 76 帧、AWP 加两颗闪光弹；1～5 人和跳号多次严格回放全部通过，游标漂移及位置误差均为 0。
- 完成 Practice / Dust2 Deathmatch 切换，最终 mode=Deathmatch、state=idle；恢复文件清除。
- 部署前已有 8 份 .ks／Defense JSON 的 SHA256 均未变化。原配置所有既有字段逐项比较保持相同，仅合并缺少的新字段；三套白名单互不继承，Versus 默认空。
- C++ BotController、NativeReplayEngine 与原版 ReplayCoordinator 未修改。

服务端汇总：`/root/keranstrat-work/v050-verification.json`。插件 data 中保留 versus-selftest-report.json、defense-selftest-report.json 与 selftest-report.json。

## 明确未完成的客户端验收

本次没有连接真人 CS2 客户端执行以下验收：彩色轮廓实际渲染、隔墙／隔烟显示、蹲跳跟随、两名观察者独立开关、未授权客户端不可见、KS/KSV 转入 Defense 后客户端无残留、游戏内改色的视觉即时效果，以及真人双阵营切队录制与烟闪弹道。

HumanClientRendered=false，ClientVisibilityIsolationTested=false。服务端原生实体属性、移除及纯权限决策通过，不等同于实际客户端渲染或网络可见性验收。发现兼容问题时管理器停止创建轮廓并清理；不以全局透视参数作替代。
