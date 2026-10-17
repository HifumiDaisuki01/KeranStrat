# 彩色 X 光观察

KS 默认关闭，KSV 默认开启。两套开关和配色互相独立；KSD 没有 X 光配置或命令，Defense 环境及会话均强制禁止生成和传输轮廓。

## 观看条件

只向对应模式白名单内、完成 Steam 认证、处于观察者阵营的真人发送轮廓，且必须正在播放对应模式。普通玩家、未认证玩家、参战真人、录制中及死斗中均不可见。HLTV／无有效观察者身份的接收端也拒绝。

KS 保留原本的播放规则：在正确阵营存活时开始 `!ks play` 或 `!ks replay`，再转为观察者观看。KSV 操作者在 play 时自动转为观察者，其他真人需提前自行转为观察者。

## 独立开关

```text
!ks xray on
!ks xray off
!ksv xray on
!ksv xray off
```

偏好按观察者和模式独立保留到插件重载；一个人关闭不影响其他人。回放结束清理轮廓，下一次回放沿用该观察者偏好。没人获准观看时不创建轮廓。

## 实时配色

```text
!ks xray colors
!ks xray color 1 FF0000
!ks xray resetcolors
!ksv xray colors
!ksv xray color t 1 FF0000
!ksv xray color ct 1 0088FF
!ksv xray resetcolors
```

颜色接受六位 RGB（可带 #），统一保存为 #RRGGBB。游戏内修改立即刷新轮廓，不重建 BOT，不停止回放。角色配色是本模式全体观察者共享的服务器配置；有对应白名单权限的账号可以修改。修改失败会明确提示，不假装保存成功。

KS 按 1～5 号配色，不区分 T/CT。KSV 按 T1～T5、CT1～CT5 配色，不按 Slot、出生顺序或当前存活人数重排。resetcolors 只恢复对应模式的出厂配色。

## 配置与持久化

现有配置新增 KsXRayDefault、KsXRayColors、VersusXRayDefault、VersusXRayColors，完整模板见 configs/KeranStrat.json。不会新增 Defense X 光字段。

游戏内配色通过原子文件替换，仅修改该模式的颜色表，保留所有白名单、其他颜色表及未知配置字段。检测到并发外部写入会拒绝覆盖并要求重试。FileSystemWatcher 在安全空闲时正常重载一次，不循环写回。文件直接编辑的配置在安全空闲后生效；播放中的即时修改请使用游戏命令。

## 实现与验证边界

使用两层 prop_dynamic 模型跟随与 Glow 属性，逐客户端 CheckTransmit 过滤；仅给合格观察者补传对应 BOT Pawn 以更新骨骼，不修改原 Pawn 的 Glow、材质、生命值或战斗 AI。结束、死亡、地图切换、卸载和 Defense 进入均清理。移除失败的实体继续保留在拒绝传输集合中并重试。

机制参考 CounterStrikeSharp 官方 CGlowProperty／CheckTransmit 接口及社区 issue #765 的模型跟随讨论：
- https://docs.cssharp.dev/api/CounterStrikeSharp.API.Core.CGlowProperty.html
- https://docs.cssharp.dev/api/CounterStrikeSharp.API.Core.Listeners.CheckTransmit.html
- https://github.com/roflmuffin/CounterStrikeSharp/issues/765

实体属性写入、权限决策和原生实体生命周期可由服务端验证。隔墙／隔烟显示、蹲跳骨骼效果、观察者独立关闭及未授权客户端不可见，仍需要真实 CS2 客户端验收；未完成的项目不会作为已通过报告。
