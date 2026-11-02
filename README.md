# 曹操传 Data 管理器源码

面向曹操传加强版 **6.4 洗隋、6.5、6.6** 的 .NET 8 WinForms 编辑器。当前源码版本为 **1.2026.1101.114**，以 DataManager.App.csproj 中的版本为准。

本仓库保留 data管理器 与同级 形象管理器 的原始目录结构。后者提供共享资源编解码、S 形象编号规则、参考图与地形编辑组件；构建时需要同时检出两个目录。

## 主要能力

- 基础数据与拓展数据编辑、字段校验、草稿和历史记录。
- CSV 与业务 XLSX 工作簿交换，包含隐藏基线、导入冲突检测和整本校验。
- 战斗公式、引擎常量与特效参数的读取、定位及受约束写入。
- 形象与资源预览、导入和迁移，参考图包交换、Mmap/Pmap 内场景地形编辑。
- 人物 R/S 形象与存档同步、查询与选中记录预览。

不同基底的字段、编码、特效分配池和可写能力分别由 Profile、签名和探测结果约束。支持版本名称不代表任意修改版均可直接写入。

## 构建与运行

需要 Windows 和 .NET 8 SDK。依赖由 NuGet 还原，包括 DocumentFormat.OpenXml、Iced 和 SixLabors.ImageSharp；相应许可说明随源码提供。

在仓库根目录执行：

~~~powershell
dotnet build data管理器/DataManager.sln -c Release
dotnet run --project data管理器/src/DataManager.App -c Release --no-build
~~~

可选的形象工具与测试工程：

~~~powershell
dotnet build 形象管理器/ImageManager.sln -c Release
~~~

## 不需要游戏基底的测试

以下入口使用合成数据或临时文件，可在 Windows 上直接运行：

~~~powershell
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --operand-sharing-safety-tests
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --record-presentation
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --resource-infrastructure
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --s-image-location
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build -- --item-types
dotnet run --project 形象管理器/src/ImageManager.Tests -c Release -- --atlas-package
~~~

其他集成测试可能需要指定的完整游戏基底、资源或外部图片样本。游戏文件和两张 Atlas 外部图片 fixture 不随源码提供；--atlas-flexible 等入口需要自行准备对应样本。默认无参运行不等于完整、独立于基底的测试入口。

## 发布

~~~powershell
dotnet publish data管理器/src/DataManager.App -c Release -p:PublishProfile=SingleFileWinX64 -o publish
~~~

当前 SingleFileWinX64 配置为 Windows x64、单文件、框架依赖发布（SelfContained=false）。运行发布程序需要 **.NET 8 Windows Desktop Runtime x64**。发布 EXE、游戏基底、运行历史和构建缓存不包含在本次源码同步中。

## 文档

- [本次源码同步与验证](data管理器/文档/源码同步-20261102.md)
- [使用说明及历史更新记录](data管理器/README.md)
- [6.4 洗隋兼容适配](data管理器/文档/6.4洗隋兼容适配.md)
- [业务工作簿交换](data管理器/文档/业务工作簿交换.md)
- [内场景地形编辑](data管理器/文档/内场景地形编辑.md)
- [图片导入兼容规则](data管理器/文档/图片导入兼容规则.md)
- [本地知识库导航](data管理器/知识库/README.md)
- [结构化曹操传 MOD 知识库](https://github.com/Artkights/Knowledge-base-for-CCZ-mod)

文档中的历史版本、发布目录和验收结果对应各自记录的源码及样本，不代表当前源码已通过游戏运行验收。修改前的早期源码另保存在标签 baseline-before-simplification-20261022。
