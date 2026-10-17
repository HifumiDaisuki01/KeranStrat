# 配置

配置位于 CounterStrikeSharp 的 `configs/plugins/KeranStrat/KeranStrat.json`；默认模板见 configs/。

| 参数 | 默认 | 作用 |
|---|---|---|
| Enabled | true | 开关；关闭时拒绝游戏指令 |
| AllowedSteamIDs | [] | 服务器认证的 SteamID64 字符串白名单；空列表不允许任何玩家 |
| DefaultMap | de_dust2 | 正常死斗默认地图 |
| DefaultGameMode | deathmatch | 正常模式，当前仅接受 deathmatch |
| MaxRecordedPlayers | 5 | 最大录制/回放人数，1–5 |
| PracticeGrenades | 五类投掷物 | kit 装备；只允许指定的烟、闪、HE、燃烧、诱饵类别 |
| AdminFlag | @css/root | 旧字段仅为兼容保留，不再参与授权 |
| DataDirectory | data | 插件目录内的相对存储路径，不允许逃逸 |
| CountdownSeconds | 3 | 录制倒计时，1–10 秒 |
| MaxRecordingSeconds | 180 | 单角色录制上限，1–180 秒 |
| MaxTacticsPerMap | 100 | 每地图归档数量 |
| MaxFileMegabytes | 64 | 单归档及展开数据上限 |
| MaxStorageMegabytes | 256 | 所有地图战术归档合计上限 |
| SourceUrl | 公开源码归档地址 | !ks source 返回地址 |
| PracticeCvars | 内置完整白名单 | 可覆盖练习值，退出恢复原值 |

PracticeCvars 默认包含 sv_cheats=1、mp_autoteambalance=0、mp_limitteams=0、mp_freezetime=0、mp_ignore_round_win_conditions=1、mp_roundtime=60、mp_roundtime_defuse=60、bot_quota_mode=normal、bot_quota=0、bot_dont_shoot=1、bot_join_after_player=0、mp_respawn_on_death_t=0、mp_respawn_on_death_ct=0、sv_hibernate_when_empty=0、mp_solid_teammates=0、ammo_grenade_limit_total=6、ammo_grenade_limit_flashbang=2、mp_buytime=9999、mp_buy_anywhere=1、mp_maxmoney=60000、mp_startmoney=60000、mp_afterroundmoney=60000。自定义键会与默认白名单合并，禁止添加任意命令。

这意味着练习时会移除正常 bot_quota 产生的 BOT，并暂时关闭友军实体碰撞；退出后恢复原来的 quota 和所有上述参数。独立练习模式不会写入竞技 cfg。武器、梯子和投掷物等精确度仍依赖游戏与原生依赖的兼容性。

桥接层另有 `KeranStrat.BotControllerBridge.json`，NativePath 相对其 ModuleDirectory，默认指向 addons/BotController/bin/linuxsteamrt64/BotController.so。

白名单示例（示例账号不能直接用于实际部署）：

```json
"AllowedSteamIDs": ["76561198000000000", "76561198000000001"]
```

配置文件改变会由 FileSystemWatcher 通知游戏线程，约 0.5 秒后在没有录制、回放、切图及文件任务时重载。空服休眠时可能延后至唤醒。已有白名单成员可 `!ks reload`；初次空白名单也可由可信服务器控制台执行 `css_ks reload`。不会要求玩家具备 RCON 或 CSSharp 管理员权限。未认证 SteamID、未知账号和禁用状态均拒绝。

不要在有人训练时热更底层程序集；普通权限和参数修改使用上述配置重载。

`!ks kit` 保留已有主武器；`!ks kit awp` 可指定主武器。配置包含 flashbang 时补给两颗，其他投掷物按类别补给。训练中开放长时、全图购买，并在补给或出生时补充资金。开始录制仅恢复起点，保留当前配装；战术同时保存投掷物堆叠数量。

训练阶段额外使用 mp_team_intro_time=0、mp_warmuptime=5、mp_warmup_pausetimer=0、mp_halftime=0、mp_maxrounds=999、mp_timelimit=0、mp_match_can_clinch=0。进入训练及训练切图时主动结束热身，并等待实际规则退出热身/冻结期。退出时恢复原值。

`css_ks_stop` 可在客户端绑定按键；`!ks trim <毫秒> [队员]` 按服务器 Tick 向上取整裁剪，至少保留一帧，裁剪后需手动保存。

训练禁用 mp_warmup_online_enabled 与 mp_warmup_offline_enabled，mp_warmuptime_all_players_connected=0、sv_warmup_to_freezetime_delay=0。进入训练时重置开局并持续核对热身、冻结状态后再开放操作。
