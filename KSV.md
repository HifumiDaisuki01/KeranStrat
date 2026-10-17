# KeranStrat Versus（KSV）

KSV 独立录制 T、CT 双方战术，以同一个服务器 Tick 时间轴回放 1～5 T 对 1～5 CT。首次创建默认 Hybrid；只由 BOT 参战，真人旁观。KS、KSD、KSV 共用服务器但不能同时占用录制器、BOT 和切图资源。

## 权限与存储

在原配置中合并 `VersusEnabled: true`，将获准账号的 SteamID64 字符串加入 `VersusAllowedSteamIDs`。发行模板默认空白名单，拒绝所有真人；不会继承 KS 或 Defense 权限。

战术保存到插件 `data/versus/<地图哈希>/<ID>.ksv`，与原版 `.ks` 完全隔离，不提供跨系统导入。共享场景库，按地图禁止重名新建。成功 new/load 的操作者持有会话，其他人不能抢占、录制、停止或切图。操作者退出或断线后释放会话；断线未保存草稿不会自动落盘。

## 独自录制双方

```text
!ksv practice
```

选择 T 并存活：

```text
!ksv new Dust2_B_3v1
!ksv anchor t
!ksv record t 1
!ksv stop
!ksv record t 2
!ksv stop
!ksv record t 3
!ksv stop
```

选择 CT，系统在普通 KSV 编辑状态安全复活，草稿及 T 方轨迹保留：

```text
!ksv anchor ct
!ksv record ct 1
!ksv stop
!ksv save
!ksv mode hybrid
!ksv play
```

播放前，其余真人须自行转为观察者；操作者播放时自动转为观察者。只生成已录制角色，不补齐人数。允许 T1/T3/T5 对 CT2/CT4。录制有倒计时及单行彩色计时，可绑定 `bind mouse3 css_ksv_stop`，与原版 `css_ks_stop` 分开。

新角色使用对应阵营 anchor，已有角色重录复用其旧起点；保留录制前自行购买的配装。重录完成后该角色的 handoff 重置为 auto，其他角色保留。射击、投掷、移动与 Subtick 使用原生缓冲数据，不采用逐点传送替代回放。

## 回放和结算

- `strict`：严格恢复录制输入，允许真实命中和击杀；每条轨迹结束后保持锁定，不寻找敌人，等待胜负或时限。
- `hybrid`：每条轨迹独立交接原生战斗 AI，保留位置、武器与剩余弹药。默认 auto 等待该角色原生回放真正结束。
- handoff 的毫秒从该角色自己的轨迹开始计算，offset 独立生效，交接时间不能超过轨迹长度。
- 单个 BOT 死亡只结束自身控制。某队全灭判对方胜，同 Tick 双方全灭判平局，其他情况到时判超时；意外断线或引擎异常取消本轮。
- repeat 开启时三秒后清理本轮 BOT 和已确认归属的临时实体，再创建双方，不逐轮重载地图。
- 准备期间禁止 BOT 自主开火，全部就绪后倒计时三秒。真人进入 T/CT 会取消模拟，不把正常玩家当成靶子。

## 指令

```text
!ksv practice
!ksv exit
!ksv map de_dust2
!ksv new <名称>
!ksv load <名称或ID>
!ksv list
!ksv save
!ksv anchor t|ct
!ksv record t|ct <1-5>
!ksv stop
!ksv remove t|ct <1-5>
!ksv offset t|ct <1-5> <毫秒>
!ksv mode strict|hybrid
!ksv handoff t|ct <1-5> <毫秒或auto>
!ksv play
!ksv repeat on|off
!ksv status
!ksv reload
```

`stop` 保留场景和 Practice。`exit`、`map` 先保存当前 KSV 场景；保存失败不会继续切换。exit 恢复 Dust2 Deathmatch。场景时限来自创建时的 VersusRoundSeconds，默认 120 秒，随场景保存。

X 光和颜色命令见 [XRAY.md](XRAY.md)。服务端空服诊断为 `css_ksv_selftest`，只允许 Dust2 Practice、无真人、无其他 BOT；内部诊断目录独立并在结束后清理。具体验证范围见 [VERSUS_TEST_REPORT.md](VERSUS_TEST_REPORT.md)。
