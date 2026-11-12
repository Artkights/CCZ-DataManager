# 曹操传 Data 管理器源码

面向曹操传加强版 **6.4 洗隋、6.5、6.6** 的 .NET 8 WinForms 编辑器。当前源码版本为三组属性筛选版 **1.2026.1112.3（2026-11-12）**，以 DataManager.App.csproj 中的版本为准。

本版提供三组“属性＋关键词”筛选，属性随当前详情子页变化。空白属性表示全部属性；各框按空格分词，词与条件组均为 AND；按回车或“查询”统一应用。每个子页独立记忆条件，“清空”立即恢复结果，快速定位保留精确跳转。详见 [使用说明及验收](data管理器/文档/三组属性筛选说明与验收-1.2026.1112.3.md)，下载见 [Release](https://github.com/Artkights/CCZ-DataManager/releases/tag/v1.2026.1112.3)。同时保留上一版 CSV 按目标字段格式导入、取消来源限制的行为。

本仓库保留 data管理器 与同级 形象管理器 的原始目录结构。后者提供共享资源编解码、S 形象编号规则、参考图与地形编辑组件；构建时需要同时检出两个目录。

本次整合以 Data 管理器 1.2026.1111.4、质变管理器 1.2026.1111.6、档位管理器 1.2026.1112.3 为基线。质变和档位共用纯 net8.0 的 `EngineGrowth.Core`；两个独立管理器仍可分别构建。

## 主要能力

- 基础数据与拓展数据编辑、字段校验、草稿和历史记录。
- CSV 按目标字段格式交换；业务 XLSX 工作簿提供隐藏基线、导入冲突检测和整本校验。
- 战斗公式、引擎常量与特效参数的读取、定位及受约束写入。
- 形象与资源预览、导入和迁移，参考图包交换、Mmap/Pmap 内场景地形编辑。
- 人物头像/R/S 下拉缩略图、统一定位按钮、三列预览，以及 R/S 形象与存档同步。
- 引擎扫描内的质变等级规则与 0～511 武器/防具引用，兵种页的全局 2～12 档设定。
- 174 个逻辑常量定义（比基线新增 86 个）、专用设定跳转与逐候选知识来源对账。
- 质变和档位补丁的空间所有权、旧 TMR1/TIER1 兼容，以及表格/特效/常量/成长草稿统一保存、回滚和历史。

不同基底的字段、编码、特效分配池和可写能力分别由 Profile、签名和探测结果约束。支持版本名称不代表任意修改版均可直接写入。

## 构建与运行

需要 Windows 和 .NET 8 SDK。依赖由 NuGet 还原，包括 DocumentFormat.OpenXml、Iced 和 SixLabors.ImageSharp；相应许可说明随源码提供。

在仓库根目录执行：

~~~powershell
dotnet build data管理器/DataManager.sln -c Release
dotnet run --project data管理器/src/DataManager.App -c Release --no-build
~~~

独立管理器与共享核心：

~~~powershell
dotnet build data管理器/src/TransformManager.App -c Release
dotnet build data管理器/src/TierManager.App -c Release
dotnet build data管理器/src/EngineGrowth.Core.Tests -c Release
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

其他集成测试可能需要指定的完整游戏基底、资源或外部图片样本。仓库包含 `ImageManager.Tests/Fixtures/Atlas` 中的两张图片 fixture，游戏文件不随源码提供；需要其他外部样本的入口仍须自行准备。默认无参运行不等于完整、独立于基底的测试入口。

## 发布

~~~powershell
dotnet publish data管理器/src/DataManager.App -c Release -p:PublishProfile=SingleFileWinX64 -o publish
~~~

当前 SingleFileWinX64 配置为 Windows x64、单文件、框架依赖发布（SelfContained=false）。运行发布程序需要 **.NET 8 Windows Desktop Runtime x64**。发布 EXE、游戏基底、运行历史和构建缓存不包含在本次源码同步中。

`data管理器/tools/Build-ReleasePackage.ps1` 按白名单生成完整源码 ZIP，并在独立解压目录构建主程序、测试项目、两个独立管理器与核心测试。`Sync-GitHubSources.ps1` 使用同一清单同步源码。构建所需的知识对账证据快照随源码保留，正常构建无需重新生成目录。

## 文档

- [四项整合使用说明](data管理器/文档/四项整合使用说明-1.2026.1112.1.md)
- [四项整合验收报告](data管理器/文档/四项整合验收报告-1.2026.1112.1.md)
- [常量迁移与知识对账](data管理器/文档/常量迁移与知识对账.md)
- [共享成长核心与补丁空间](data管理器/文档/共享成长核心-质变与档位.md)
- [使用说明及历史更新记录](data管理器/README.md)
- [6.4 洗隋兼容适配](data管理器/文档/6.4洗隋兼容适配.md)
- [业务工作簿交换](data管理器/文档/业务工作簿交换.md)
- [内场景地形编辑](data管理器/文档/内场景地形编辑.md)
- [图片导入兼容规则](data管理器/文档/图片导入兼容规则.md)
- [本地知识库导航](data管理器/知识库/README.md)
- [结构化曹操传 MOD 知识库](https://github.com/Artkights/Knowledge-base-for-CCZ-mod)

文档中的历史版本、发布目录和验收结果对应各自记录的源码及样本，不代表当前源码已通过游戏运行验收。修改前的早期源码另保存在标签 baseline-before-simplification-20261022。

四项整合版 1.2026.1112.1 的 Release EXE 在 6.4、6.5、6.6、6.6 国庆四个样本上各通过 18 项只读内置验收，写入测试均使用副本。该版实际测试宿主和发布 EXE 的 `DeviceDpi` 为 144；100%/125%/150% 为字体倍率测试，未覆盖 96/120 DPI、跨屏或游戏内实战。本次 CSV 版本的验证范围见上方使用说明及 Release 附件中的验证记录。
