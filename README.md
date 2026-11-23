# ClipDesk

以剪贴板为首页的 Windows 个人效率工具。当前为 0.4.0 原生桌面预览版，修复和验收结果以 `docs/0.3.0更新说明.md`、`docs/验收记录.md` 为准。七牛真实接口、两机同步和跨应用自动粘贴尚待实测。

本项目位于既有 Snapaste 仓库内的独立目录，不改动截图工具的代码、数据或发布包。ClipDesk 只维护一套实现。

## 运行与构建

开发环境使用 .NET 10 SDK、PowerShell 7，Windows 10/11 x64。构建入口为 `build.ps1`，运行产物为 `artifacts/ClipDesk/ClipDesk.exe`。

完整便携包：`artifacts/ClipDesk-0.4.0-win-x64.zip`。解压后双击 `ClipDesk.exe`，不需要另外安装 .NET。操作说明见 `使用说明.md`。

中文安装包构建入口为 `package.ps1`（官方 NSIS 3.13 ZIP 编译器），输出 `artifacts/ClipDesk-0.4.0-win-x64-setup.exe`。存在 `D:\APP` 时优先安装到 `D:\APP\ClipDesk`，否则使用当前用户程序目录，不请求管理员权限。退出正在运行的 ClipDesk 后再更新，安装器不会强行关闭编辑窗口；卸载保留用户数据。本机自动安装结果见 `docs/本机安装记录.md`。

```powershell
pwsh -File .\build.ps1 -Portable -Verify
```

可选 `-NativeChecks` 先建立独立的非交互 Windows 窗口站，再检查合成剪贴内容；无法确认隔离就中止，不写当前用户剪贴板。它不替代真实前台鼠标、输入焦点和第三方软件人工验收。

编辑器与草稿检查可单独执行 `tests/ClipDesk.EditorChecks/run.ps1`，使用隔离数据库，不写系统剪贴板。

默认快捷键为 `Ctrl+Shift+V`。所有数据首先保存到本机 SQLite；七牛配置与凭据未填写时不访问云端。

## 项目文档

- `docs/需求与技术决策.md`：本期范围、底座审查、数据与同步设计。
- `docs/开发进度.md`：已完成内容及下一步。
- `docs/验收记录.md`：实际执行的检查、结果和未验证范围。
- `docs/问题审查清单.md`：角色推演、独立回归和遗留问题。
- `docs/0.3.0更新说明.md`：四窗口、透明度、快捷键和粘贴改进。

用户数据默认位于 `%LOCALAPPDATA%/ClipDesk`。开发验收使用 `CLIPDESK_DATA` 指定隔离目录。
