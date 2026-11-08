# DeskIsle

Windows 11 x64 桌面文件夹卡片工具。当前交付 **0.1.0 单卡片技术原型**，用于验收桌面层级后继续实现完整首版。

## 运行

解压 `artifacts/DeskIsle-0.1.0-win-x64.zip`，双击目录中的 `DeskIsle.exe`。这是自包含目录版，请保留同目录全部文件；无需安装 SDK 或运行时。未经签名，首次运行可能出现 Windows 提示。

选择目录后显示直接子项；双击调用系统默认程序打开，目录交给资源管理器。右键可打开、打开所在目录或刷新。标题栏拖动，边缘缩放，底部调节背景透明度，右上角退出。此原型不改动、移动或删除展示的文件；默认隐藏系统和隐藏项目。

启动尝试桌面模式，失败明确降级；可手动切换浮动模式或重试桌面模式。桌面模式依赖未公开的 Explorer WorkerW 层级，不保证所有 Win11 版本行为相同。普通浮动模式不是桌面嵌入，Win+D 可能隐藏卡片。未通过用户实测前，不将桌面附着成功等同于兼容性通过。

## 开发与发布

- `src/DeskIsle.Core`：可跨平台的直接目录读取和排序。
- `src/DeskIsle`：WPF 窗口及隔离的 Windows 桌面宿主。
- `tests/DeskIsle.Core.Tests`：临时目录自动化测试。
- `scripts/publish.ps1`：Windows 自包含发布和 ZIP 打包。

锁定 SDK 10.0.403。下载官方 Windows x64 SDK ZIP，验证官方 SHA-512 后解压到 `.tools/dotnet-win`。SDK 下载地址及散列可在官方发布元数据查找：
https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json

在 Windows PowerShell 从仓库根目录运行：

```powershell
& ./.tools/dotnet-win/dotnet.exe test tests/DeskIsle.Core.Tests/DeskIsle.Core.Tests.csproj -c Release
./scripts/publish.ps1
```

WSL 可通过 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$PWD/scripts/publish.ps1")"` 调用 Windows 发布脚本。不要同时运行发布和测试，二者共享核心库构建目录。

## 当前边界与后续

当前没有配置保存、多卡片、托盘、单实例、自动文件同步、图标、网格、开机启动和找回卡片。退出后位置不保存。桌面模式、半透明效果、DPI、Explorer 重启及多屏均待用户验收；详细步骤见 `docs/阶段一验收.md`。

下一阶段在桌面原型反馈基础上完成多卡片、持久化、托盘、单实例和异常恢复；最后完成网格、图标、监听、吸附和完整设置。

原型退出后删除解压目录即可移除；当前不写用户配置、不创建开机启动入口。
