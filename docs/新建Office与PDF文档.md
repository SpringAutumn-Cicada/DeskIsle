# 0.5.8 新建Office与PDF文档

卡片空白右键→新建增加Word(.docx)、Excel(.xlsx)、PDF(.pdf)、PowerPoint(.pptx)，复用现有名称/保存位置/同名保护。嵌入真实格式空白模板：Word空段落、Excel一个空工作表、PDF一页空白A4、PowerPoint一张空白幻灯片；不是零字节改后缀。创建无需安装Office，打开/编辑仍需关联程序，PDF是否可编辑由关联软件决定。

模板是应用源码资源，不含宏、用户文件或个人身份信息。运行时无额外第三方依赖。模板生成工具为scripts/create-document-templates.py，依赖Python文档库，仅在维护模板时使用；临时生成依赖放.tools内，不修改全局配置，不随应用发布。模板资源缺失先报错不创建目标；现有CreateNew拒绝覆盖仍有效。

变更：新建菜单、NewItemService、弹窗标题、嵌入资源和四个模板源码资产、生成脚本及版本说明。未运行自动化或Windows Office/PDF打开验收。待用户用临时目录检查四种创建/打开、同名冲突、取消及文件内容为空白。
