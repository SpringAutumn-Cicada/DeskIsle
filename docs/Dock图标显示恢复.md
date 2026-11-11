# DeskIsle 1.0.4 图标显示恢复

按用户要求仅恢复图标显示，暂不开发缩到Dock图标的最小化动画。

- 以1.0.2实现85aef8c为基准恢复DockApps.cs图标提取和DockWindow.xaml显示效果。
- DockWindow.xaml.cs只恢复96～256像素请求和原有1.12倍悬停显示；不回退1.0.3按下前台快照、禁止激活、最小化命令与窗口归属判断。
- 保留控件复用、稳定排序、自动启动和空桌面常驻。
- 修改文件：DockApps.cs、DockWindow.xaml、DockWindow.xaml.cs、DeskIsle.csproj、README及本记录。
- 用户配置和真实文件未修改，未运行新版或关闭旧版。

本轮不执行自动化测试或Windows交互验收，只执行交付构建、包完整性与复制哈希校验。图标大小和清晰度待用户验收。

## 实际发布

分支 test，实现提交 d934f90。项目专用 .NET SDK 10.0.403；`scripts/publish.ps1` 最终退出码0。桌面包 `E:\桌面\DeskIsle-1.0.4-win-x64.zip`，63356764字节，ZIP CRC正常，共403项，含EXE和恢复脚本。原包与桌面副本SHA256一致：`ee78cf39d3f1e7bf3ad6051538994152d1a69b60c78411238cf2c977c267e2e0`。

`git diff --check` 无格式错误；图标提取和XAML布局与1.0.2比较无差异。未执行自动化测试或Windows功能验收；没有启用自定义最小化动画。
