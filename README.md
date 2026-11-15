# 驱界 · 完整源代码

本目录包含「驱界」v1.0.0 的全部源代码：

- `src/QuJie.App`：WPF 宿主、WebView2 桥接、设备驱动扫描、Windows Update 驱动更新、离线驱动导入
- `src/QuJie.App/ui`：HTML / CSS / JavaScript 界面
- `assets`：应用图标与界面资源
- `InstallerSource`：单文件安装程序与卸载程序的完整源码
- `使用说明.txt`：应用使用说明

## 运行环境

- Windows 10 / 11 x64
- .NET 10 SDK（编译时）/ .NET 10 Desktop Runtime（运行时）
- Microsoft Edge WebView2 Runtime

## 编译应用

在源码根目录执行：

```powershell
dotnet build src/QuJie.App/QuJie.App.csproj -c Release
dotnet publish src/QuJie.App/QuJie.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

生成的应用位于 `publish/驱界.exe`。应用清单使用
`requestedExecutionLevel level="requireAdministrator"`，启动时会请求 UAC 管理员权限。

## 编译安装程序

1. 将编译好的应用文件夹放到桌面 `驱界`，或设置环境变量：

   ```powershell
   $env:QUJIE_APP_DIR = "D:\build\publish"
   ```

2. 从源码根目录执行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File InstallerSource\build.ps1
   ```

   如果你在开发仓库里（目录名为 `installer-src`），把上面的路径换成
   `installer-src\build.ps1`。

3. 安装包输出到 `dist\驱界-安装程序-v1.0.0.exe`。

安装程序使用 .NET Framework 4.x 自带的 `csc.exe` 编译，不需要额外安装 Inno Setup 或 NSIS。

## 安全设计

- 安装器只允许安装到空专用文件夹或已有驱界安装标记的目录，拒绝桌面、文档、下载、Windows 等保护目录。
- 安装器把应用和完整源码写入安装目录，并写入隐藏安装清单。
- 卸载器在确认安装标记和安全路径后，会清空整个安装目录，包括 `Source`、手动放入的文件和所有子目录。
- 卸载不会触碰安装目录之外的文件；应用数据目录 `%LOCALAPPDATA%\QuJie` 不受影响。
- 安装器本身不需要管理员权限；驱界主程序需要管理员权限，启动时由 Windows UAC 提示。

## 说明

驱界是本地系统工具。Windows Update 驱动更新需要网络和管理员权限；厂商专属驱动如果未通过 Windows Update 发布，请使用厂商官方驱动或“从文件夹安装驱动”。
