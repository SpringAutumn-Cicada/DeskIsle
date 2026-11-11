# 发现与实现依据

- DockApps.Icon 通过 SHGetFileInfo 请求大图标后指定64像素创建位图，实际来源常为32像素，放大导致模糊。
- 微软 IShellItemImageFactory.GetImage 文档：ICONONLY=4；BIGGERSIZEOK=1 避免 Shell 先缩小图标；由调用方 DeleteObject 释放 HBITMAP，图标提取在后台执行。
  https://learn.microsoft.com/windows/win32/api/shobjidl_core/nf-shobjidl_core-ishellitemimagefactory-getimage
- EnumWindows 结果顺序随 Z-order 改变，SetApps 直接用枚举结果重建运行区造成位置变化。固定应用与运行应用分别维持稳定顺序，运行区新应用追加，关闭应用正常移除；窗口切换不再改变排序。
- 当前设置窗口仍使用系统标题栏、默认滑块与组合框，本轮改成圆角深色控件。
- 本轮用户明确覆盖上一版首次关闭策略：每次程序启动自动启用 Dock，损坏或未知配置仍不接管。

首次发布构建发现 XAML 默认 public 与设置类 internal 冲突，已在 XAML 显式声明 x:ClassModifier=internal。标题拖动排除关闭按钮，设置内容增加滚动以适配小工作区。
