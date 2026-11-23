# 来源与第三方组件

ClipDesk 是个人 Windows 桌面工具，独立实现。本轮没有复制 EcoPaste 或 Snipaste 的源码、图标、品牌与资源。

EcoPaste 用作底座可行性审查：<https://github.com/EcoPasteHub/EcoPaste>，提交 `5139d30b0f4c1309356a9b308c05092f1038bc9b`，Apache-2.0。审查范围及未采用原因见技术决策文档；其源码不包含在便携包中。

| 组件 | 固定版本 | 用途 / 上游许可 |
|---|---|---|
| .NET / Windows Desktop Runtime | 10.0.12 | WPF、Windows Forms 托盘、标准加密库；MIT 及其第三方通知 |
| Microsoft.Data.Sqlite / Core | 10.0.12 | SQLite 接口；MIT |
| SQLitePCLRaw bundle / provider / core / native | 2.1.12 | SQLite 绑定及原生库；Apache-2.0 / SQLite 公有领域及其上游通知 |
| AWSSDK.S3 | 4.0.104.12 | S3 协议与原生 V4 签名；Apache-2.0 |
| AWSSDK.Core | 4.0.103 | AWS SDK 核心；Apache-2.0 |
| NSIS | 3.13 | 中文安装器，使用 zlib 压缩；zlib/libpng 许可，原文见 `licenses/NSIS-COPYING.txt` |

传递依赖与完整版本以 `packages.lock.json` 为准。原文许可随 `licenses/` 分发，保留各自版权说明。图形界面使用系统字体和系统托盘图标，不包含 EcoPaste 的美术资源。
