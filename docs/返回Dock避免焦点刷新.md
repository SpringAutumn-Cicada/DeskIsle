# 1.11.16 返回 Dock 避免焦点刷新

- test 分支，基于 3e75a19；工作区干净，专用 SDK 已安装。核对返回入口、显隐动画、任务栏租约及桌面卡片恢复调用链。
- 返回窗口原先仅 ShowActivated=false，鼠标点击仍可能激活窗口，引发 DesktopReturnMonitor 的前台切换恢复与卡片重绘。现在在首次显示前设置 WS_EX_NOACTIVATE / TOOLWINDOW，并在 WM_MOUSEACTIVATE 返回 MA_NOACTIVATE，与主 Dock 一致。
- 返回仍只切换任务栏、隐藏机器人并调用统一 RevealAnimated；不创建新 Dock、不刷新卡片，不引入另一套返回动画。
- TaskbarLease.Native(false) 原先先隐藏再 Reconcile 再次隐藏，改为单次协调；显示原生任务栏只请求显示未显示窗口。
- 保留普通显隐动画、机器人拖动位置保存、右键关闭及必要的桌面恢复能力，不禁用真实 Win+D 后的卡片恢复。
- 未运行测试、lint、独立类型检查或 Windows 交互验收；仅执行授权发布和包校验。
- 待用户验收：返回 Dock 与底边唤出动画一致性、卡片不随点击刷新、原应用焦点保留、拖动和任务栏来回切换。

## 发布记录

- 功能提交 43648be，版本 1.11.16。
- 专用 SDK Windows x64 自包含发布成功；ZIP 405 项 CRC 校验通过，主程序、恢复脚本及 WinRT 依赖存在。
- 已复制至 `E:\桌面\DeskIsle-1.11.16-win-x64.zip`，保留旧包；源包与副本 SHA-256 一致：`ea509212851c2f2339254ab65a6abc9c28e4b57ac80175a7f2631a832abf38f0`。
- 未执行测试或 Windows 交互验收，返回显现及卡片稳定性待用户验收，无合并、无推送。
