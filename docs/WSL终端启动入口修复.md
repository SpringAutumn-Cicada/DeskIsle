# 1.11.19 WSL 终端启动入口修复

- test 分支，基于 df475ce；工作区干净，专用 SDK 已安装；核对启动、固定及快捷方式解析流程和历史。
- 报错原因：直接执行 C:\Program Files\WindowsApps 下版本化商店 WindowsTerminal.exe 被拒绝访问，不能将运行时映像路径等同于正常启动入口。
- 增加启动入口解析：仅针对 WindowsApps/Microsoft.WindowsTerminal 包内的 WindowsTerminal.exe，改用当前用户 Microsoft/WindowsApps/wt.exe 执行别名；工作目录使用用户目录，不继承便携包目录。所有经统一 Launch 的打开/新窗口入口生效，旧固定项不用重新固定。
- 用户环境只读核对：wt.exe 别名存在；Terminal defaultProfile 对应 Microsoft.WSL 的 Ubuntu-24.04。因此通过 wt.exe 沿用默认配置，不硬编码发行版、不改全局或用户终端配置。
- 普通 exe、lnk、WSL 快捷方式和其他 URI 保留原始启动方式，快捷方式参数不重写。执行别名缺失时提示启用或改固定官方快捷方式，不退回被拒绝访问的路径。
- 依据：[微软 Windows Terminal 命令行文档](https://learn.microsoft.com/en-us/windows/terminal/command-line-arguments)。
- 未运行测试、lint、独立类型检查或 Windows 交互验收；只读检查配置不代表启动验收。仅执行授权的发布构建及包校验。
- 待用户验收：关闭全部终端后从已有 Dock 固定图标启动 Ubuntu-24.04、右键新窗口、已有窗口激活及发行版正常进入。未来默认配置改变时将按用户新默认配置启动。
