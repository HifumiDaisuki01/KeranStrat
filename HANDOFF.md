# KeranStrat 交接 — 2026-10-21

备份前 main 为 013e85d（KS/KSV/KSD 网页帮助）；此前正式服版本记录为插件 0.5.5、BotController API ABI22、原生 0.7.2。当前备份不重新部署或测试正式服。

## 当前状态

KS/KSV/KSD 现有功能和 BOT 管理保持原样。帮助页面为 https://cs.jinhsi.cn/help，进入指令提示引导玩家访问网页；无需强制打开客户端浏览器。

服主曾报告 ksv load 无响应。只读调查发现旧战术 GameVersion=ServerVersion=2000933，而更新后为 2000934，tick interval 均 0.015625。代码对版本严格比较；异步加载失败时通知所需 owner 可能尚未建立，错误仅记日志。新录制战术可用。**服主明确说 KSV 暂不需要改，因此没有修复或改写旧战术元数据。**

10052 人物 T 方手臂问题属于独立模型项目，与 KSV 无关。不得借此改动 BOT AI、回放文件格式、战术数据库或 KSD 回合复活规则。

## 恢复与构建

src 为托管源码，third_party/refs 为依赖与参考，scripts 为构建部署，tests 为测试，web 为帮助站。具体构建入口以仓库 README、项目文件及 scripts 为准；先查看 dotnet --info 和现有依赖版本，不能猜测全服 API。

Windows Desktop/KeranStrat、KeranStrat-Publish 是独立工作副本，已按原路径备份。原有 .ks/.ksv 格式保留，不能批量改 GameVersion 来绕过未经验证的兼容性。

## 下一位立即执行

先从 Git 与私有备份还原并比较差异。没有待执行的 KSV 修复授权。后续任何训练工具必须尊重 KeranStrat 对 BOT 和 KSD CT 统一复活的所有权。不要将 KeranPractice 的个人清理扩大为全图删除。
