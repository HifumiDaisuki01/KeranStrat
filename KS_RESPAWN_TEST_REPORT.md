# KS 切队复活修复验证（0.4.1）

基于 main a833364。检查 PracticeEnvironment.cs 后保留双方 mp_respawn_on_death_* = 0：全局开启会让录制 BOT 和 Defense 死亡目标提前复活。仅普通 KS 的已认证、已授权真人收到 player_team 后进入单次延迟复活队列。

编译：0 警告、0 错误。37 项独立检查通过，其中新增四项覆盖 T→CT→T、单次执行、引擎事件帧仍显示旧阵营/存活状态、快速切队、重复请求、观察者、过期、身份复用、模式退出和 Defense 空闲会话/运行状态隔离。原有存储、严格回放和 Defense 核心检查仍通过。

已部署至目标 CS2 服务器，三个插件文件与构建产物完全一致，插件列表显示 KeranStrat 0.4.1。实际完成 Deathmatch → Practice → Dust2 Deathmatch 模式切换，Practice 自动复活参数仍为关闭，退出后恢复文件清除。8 份现有战术／场景配置／插件配置 SHA256 均未变化。没有连接真人客户端：真实 T→CT→T 后立即录制，以及 KSD 被 BOT 击杀后不提前复活、队友继续和下一轮统一复活，尚未完成本版本客户端验收，不以独立检查替代实测。

未修改原生 BotController、NativeReplayEngine、Defense 回合管理器或 AI 交接逻辑；保留原版战术格式。回退时仅还原备份的插件文件，保留配置和 data。
