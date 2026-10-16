# 第三方组件

## CS2-Bot-Controller / CS2-Bot-Mimic2

- 作者：XBribo 与上游贡献者；完整原始作者信息保留在 third_party/CS2-Bot-Controller/。
- 版本：0.7.2，ABI 22。TECH.md 的“21”与该发布版实际 NativeApi/exports 不一致，实际校验 ABI 22。
- 上游：https://github.com/XBribo/CS2-Bot-Controller/tree/v0.7.2
- 许可证：AGPL-3.0-only；保留 LICENSE 和 LICENSE.AGPL3。libraries/ 的原有许可证保持不变。
- 本项目复用其原生发布二进制、共享契约和未经修改的 NativeApi/ApiImpl 源码；桥接与业务编排由 Keran Technology 实现。
- 不能将第三方原生录制引擎宣称为 Keran Technology 完全原创。

## CounterStrikeSharp

- 作者：Roflmuffin 与贡献者。
- 本服务器实际使用：1.0.376 @ 653d651，.NET 10。
- 源码：https://github.com/roflmuffin/CounterStrikeSharp
- 许可证：GPL-3.0；引用目标服务器程序集，无修改。

## PracLab

需求指定的 https://github.com/MEngYangX/PracLab 在本次审查时返回 404，未复制或使用其源码。

KeranStrat 自身采用 AGPL-3.0-only。发布归档包含对应源码、修改、构建说明与第三方声明；部署时向使用者提供 !ks source 指向的归档。
