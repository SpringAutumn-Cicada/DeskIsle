# 1.11.7 WinUI 托盘原生读取通道

- 分支 test；基于 b19a7f2，开始时工作区干净，项目专用 SDK 已安装。
- 实际只读诊断：用户当前 Windows 10.0.26300，1.11.6 记录阶段 no-accessible-tray-items，候选数组为空。原生窗口枚举找到 Shell_TrayWnd、TopLevelWindowForOverflowXamlIsland 及 Windows.UI.Composition.DesktopWindowContentBridge。旧 System.Windows.Automation 接口对任务栏与溢出根的无筛选 Descendants 均返回 0。没有执行点击、展开或应用恢复验收。
- 新增 Windows COM CUIAutomation8 原始树读取，显式读取任务栏、溢出窗口及内部 Composition bridge HWND；不再仅依赖旧托管 UIA 入口。保留托管读取作为后备。
- 限定通知区域识别应用，兼容 Button/MenuItem/Custom 控件，过滤不可见与禁用项目；唯一明确进程或产品名匹配后操作真实图标，歧义仍选择目标。
- 自动展开已识别的托盘箭头，等待图标载入；鼠标命中元素或其子元素才双击，默认动作作为后备。恢复结果仍以已有应用窗口可见、可操作及获得前台为准。
- 原生 COM 接口按 Microsoft WinSDK UIAutomationClient.h 的 IID、方法槽位声明；COM 引用限定本次后台请求并清理。读取有节点/深度和时间限制，不阻塞 Dock UI。诊断阶段区分原生与托管空树并记录节点数，不记录托盘原文。
- 源码依据：[Microsoft UI Automation 原始树说明](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-treeoverview)、[微软 WinSDK 接口定义](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/UIAutomationClient.h)。
- 开发验证：只读查看诊断与窗口结构；不代表恢复通过。未运行测试、lint、独立类型检查或 Windows 交互验收。
- 遗留验收：原生通道在此 Windows 版本能否取得实际图标、微信/钉钉关闭到托盘后的反复图标与预览恢复、多实例选择及权限差异。未经实际操作，不能将这些项目写为通过。

## 发布交付

- 功能提交 e4b826f、64f31e6。最终代码完成后重新发布，版本 1.11.7，Windows x64 自包含便携包。
- 发布构建成功；ZIP 405 项 CRC 校验通过，主程序、恢复任务栏脚本及 WinRT 依赖存在。
- 已复制到 `E:\桌面\DeskIsle-1.11.7-win-x64.zip`，旧版本保留；源包和副本 SHA-256 一致：`95d69c6d8e30952fc9d3916f80552b01da92d70e0c9637a4c042c543689e2caa`。
- 未运行自动测试或 Windows 功能交互验收；上述只读诊断不等于应用恢复验收通过。
