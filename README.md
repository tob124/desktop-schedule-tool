# desktop-schedule-tool

基于 XLSX 的 Windows 桌面日程小工具。可在桌面常驻、切换工作表和滚动浏览，保留工作簿的单元格格式。挂件独立运行，入口在系统托盘。

## 开始使用

1. 从 [GitHub Releases](https://github.com/tob124/desktop-schedule-tool/releases) 下载 Windows x64 ZIP，将全部内容解压到一个固定文件夹。
2. 双击 `ScdDesktop.exe`，选择一个本地 Excel 工作簿（`.xlsx`）。
3. 从工具栏 `⋯` 或托盘打开「设置」，可启用登录时启动，调整不透明度、背景模糊和衬底颜色。

按 `F11` 切换「仅浏览」。仅浏览模式会隐藏标题栏和上下工具栏，右上角的 `⋯` 可切换工作表或恢复工具栏。也可在设置中单独隐藏常规模式标题栏。无标题栏时，按住 `Alt` 拖动窗口，或使用仅浏览菜单里的拖动按钮。

工作簿以只读方式打开。保存工作簿后，挂件会重新读取，浏览位置和外观设置保存在本机 `%LOCALAPPDATA%\ScdDesktop`。桌面模式跟随 Windows 桌面一起切换。打开设置、选择工作簿或展开时，挂件会暂时作为独立窗口显示。衬底颜色会在表格区域重绘时保留。

## 系统要求

- 64 位 Windows 10 或 Windows 11
- .NET Framework 4.8 和 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
- 亚克力背景模糊需要支持系统背景材质的 Windows 11，并受 Windows 透明效果设置影响。

## 从源码构建

在 Windows x64 上安装 Node.js 20 或更新版本，再运行以下 PowerShell 命令。

```powershell
.\build.ps1
```

构建脚本会获取锁定版本的 npm 依赖和 WebView2 SDK，生成 `bin\ScdDesktop.exe` 及配套文件。挂件使用 [xlsx-vanilla-viewer](https://www.npmjs.com/package/xlsx-vanilla-viewer) 显示表格，第三方许可证和声明随构建产物放在 `bin\licenses`。
