# 原因

- PointerTick 只有鼠标/菜单停留条件，未考虑无普通应用窗口的桌面状态。
- Click 只有 Activate，缺少前台窗口最小化分支；Dock 被鼠标激活后前台会变为 Dock，需要在 WM_MOUSEACTIVATE 阶段记住原前台，或使 Dock 不激活。
- SetApps 的签名包含前台和标题，变化即 Apps.Children.Clear 全量重建。鼠标下控件反复被替换，MouseEnter 重复触发。

采用复用控件、动态更新窗口数据与指示点；Dock WM_MOUSEACTIVATE 返回 MA_NOACTIVATE，点击不抢走原窗口前台。多窗口应用前台属于本组时最小化该窗口，其余情况保留窗口选择列表。
