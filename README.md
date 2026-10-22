# 曹操传 Data 管理器源码

保留原始源码目录结构。`data管理器` 是主程序，`形象管理器` 提供构建所需的共享资源编解码与 UI 源码。

```powershell
dotnet build data管理器/DataManager.sln -c Release
dotnet run --project data管理器/src/DataManager.Tests -c Release --no-build
dotnet publish data管理器/src/DataManager.App -c Release -p:PublishProfile=SingleFileWinX64 -o publish
```

需要 Windows、.NET 8 SDK；框架依赖发布程序运行需要 .NET 8 Windows Desktop Runtime。游戏数据、旧发布程序与历史备份不包含在源码仓库中。
