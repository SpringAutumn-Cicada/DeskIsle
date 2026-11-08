# DeskIsle

Windows 11 x64 桌面文件夹卡片工具。当前源码为 **0.2.0 收藏卡片**，支持直接拖入引用和矢量图标；已生成 Windows x64 自包含包，Windows交互效果待用户验收。

## 运行

解压 `artifacts/DeskIsle-0.2.0-win-x64.zip`，双击目录中的 `DeskIsle.exe`。这是自包含目录版，请保留同目录全部文件；无需安装 SDK 或运行时。未经签名，首次运行可能出现 Windows 提示。

默认直接将文件或文件夹拖入空卡片，原文件位置、内容和属性保持不变。也可点击标题右侧“•••→浏览目录”查看目录直接子项；“收藏卡片”返回已保存引用。单击选中且不自动滚动，双击调用系统默认程序打开，目录交给资源管理器。右键可打开、打开所在目录或刷新。标题栏拖动，边缘缩放；透明度、显示模式、刷新和退出都在“•••”菜单中。图标为按类型配色的矢量图形，不改变系统图标、不生成文件缩略图。右键可移出引用或重新定位。此原型不改动、移动或删除展示的文件；默认隐藏系统和隐藏项目。

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

当前已支持引用及布局保存，配置位于 `%LOCALAPPDATA%\DeskIsle\card.json`。底部按钮仅在主动点击后隐藏全部原生桌面图标；再次点击恢复，正常退出恢复，异常退出后重启尝试恢复。更多菜单提供恢复入口。隐藏状态不作为偏好保存。

0.2.0已完成构建和自包含发布，发布包放到Windows桌面供用户测试。以后每次功能或修复交付自动生成新包；自动测试和Windows交互验收另行记录，不把构建成功当作功能验收通过。

桌面嵌入仍存在兼容限制，窗口可降级为浮动模式；多卡片、托盘、持续文件监听等尚未完成。详细操作记录和待验收项目见 `docs/收藏卡片交付.md`。

移除程序前先通过按钮恢复桌面图标并正常退出，然后删除发布目录；删除 `%LOCALAPPDATA%\DeskIsle` 会清除收藏和恢复标记，请在确认图标已恢复后操作。程序不创建开机启动入口。
