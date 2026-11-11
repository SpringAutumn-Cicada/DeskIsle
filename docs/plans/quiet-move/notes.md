# 现状与实现

MainWindow.Move.cs先弹MoveConfirmation，再弹TransferProgressWindow，导致无冲突也打断拖放。MainWindow.Import.cs、NewItem.cs等把完成/跳过/迁移统计写到卡片Status；MigrationNotice还占标题下的一行。

改为同一FileTransfer服务异步启动：快速成功不创建可见进度窗；移入不自动显示普通进度，长任务从菜单主动查看；导入/新建超过350ms才显示进度。同名冲突仍由TransferConflict覆盖/跳过/取消处理。普通成功清空Status，错误反馈保留，不将文件失败伪装为成功。

结果：0.7.1发布与桌面复制完成，402条目CRC/运行库/SHA256校验通过；未执行测试或实际文件移动。详见docs/简洁卡片与直接移入.md。
