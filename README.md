# 曹操传 Data 管理器源码

面向曹操传加强版 6.5 / 6.6 的 .NET 8 WinForms 编辑器。当前版本 **1.2026.1022.6**。

本版统一名称、列传、说明、台词、锻造名称及物理表文本输入的容量提示，显示“剩余 xx 字节”和“1个汉字2字节，1个字母/数字1字节”；提示根据实际宽度换行，编辑区按所需高度排列。详见 [字节提示说明与验收](data管理器/验证/2026-10-22-字节提示说明/验收说明.md)。

本版移除“人物/套装特效”模块及专用实现，保留其他业务功能；数据、形象和迁移历史统一放到 EXE 同级的 `项目名称_历史记录/完整项目路径标识/`，同名不同路径的项目分别隔离，并兼容原项目内旧历史。

仓库保留 `data管理器` 与同级 `形象管理器` 的原始目录结构，后者提供共享资源编解码与 UI 源码。修改前完整源码保存在标签 `baseline-before-simplification-20261022`。

```powershell
dotnet build data管理器/DataManager.sln -c Release
$testBase = "C:\测试基底\加强版6.5未加密版" # 改为本机实际的完整 6.5 测试基底目录
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --core-regression $testBase
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --history-location $testBase
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --ui-writeback-hardening $testBase
dotnet publish data管理器/src/DataManager.App -c Release -p:PublishProfile=SingleFileWinX64 -o publish
```

需要 Windows 和 .NET 8 SDK；框架依赖发布程序运行需要 .NET 8 Windows Desktop Runtime x64。游戏文件、运行历史和发布 EXE 不包含在源码仓库中。

- [使用说明](data管理器/README.md)
- [字节提示说明与验收](data管理器/验证/2026-10-22-字节提示说明/验收说明.md)
- [模块精简与历史隔离](data管理器/文档/模块精简与历史隔离.md)
- [本次验收报告](data管理器/验证/2026-10-22-模块精简与历史隔离/验收报告.md)
- [独立审查与回归](data管理器/验证/2026-10-22-模块精简与历史隔离/独立审查.md)
