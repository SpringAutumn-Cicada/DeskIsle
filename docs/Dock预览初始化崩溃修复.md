# DeskIsle 1.2.3 预览初始化崩溃修复

Windows Application日志事件1026确认1.2.2未处理异常：InvalidOperationException「此 Visual 未连接到 PresentationSource」。堆栈为WindowPreview.UpdateThumbnails → WindowPreview构造函数 → DockBar.ShowPreview，发生在预览创建阶段。

上版新增隐藏状态下UpdateThumbnails(false)准备画面，但该方法同时对未显示的子控件调用PointToScreen进行悬停判断；EnsureHandle仅创建原生句柄，不保证子Visual连接显示源。

修复：隐藏准备阶段不进行鼠标屏幕坐标判断；显示后亦确认Host连接PresentationSource才转换坐标。定位锚点同样检查显示源。保留隐藏准备DWM布局、画面稳定显示和蓝灰反馈，不以吞掉全局异常掩盖问题。

修改WindowPreview.cs、项目版本和README；未直接修改用户配置、文件或系统状态。

本轮实际读取现有错误日志、检视代码；不运行自动化测试或Windows交互验收。需用户验证悬停、点击预览、关闭/翻页、组合恢复不再退出。构建与包校验不等于功能验收通过。

## 实际发布

分支test，实现d82588d。项目专用SDK10.0.403，发布脚本最终退出码0。桌面包`E:\桌面\DeskIsle-1.2.3-win-x64.zip`，63367244字节，共403项，ZIP CRC正常，源包与桌面副本SHA256一致：`c8800fc6cdfec27f384d287beebea1271f21e34062f38bc1fd44664544acd841`。`git diff --check`无格式错误。未执行自动化测试或Windows点击验收。
