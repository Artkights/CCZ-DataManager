using System.Data;
using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;
using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class BusinessTableWriteTests
{
    public static void Run(string basePath)
    {
        var root = TestSupport.CreateCoreCopy(basePath);
        try
        {
            var workspace = new ProjectWorkspace();
            workspace.Open(root, "ccz-6.5", "65-standard-v1");
            var before = new[] { "Data.e5", "Star.e5", "Ekd5.exe", "Imsg.e5" }
                .ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
            var pages = new BusinessDataService(workspace);
            var transaction = pages.BeginTransaction(BusinessPageKind.PhysicalTables, "业务物理表跨模块写回复读");
            var expected = new List<(string Key, int Row, string Field, object Value)>();
            void Set(string key, int row, string field, object value)
            {
                transaction.Set(key, row, field, value);
                expected.Add((key, row, field, value));
            }
            void Toggle(string key, string field, int mask = 1)
                => Set(key, 0, field, Convert.ToInt32(workspace.GetDocument(key).Data.Rows[0][field], CultureInfo.InvariantCulture) ^ mask);

            Toggle("job-series-counter", "对兵种系 1");
            Toggle("job-series-terrain", "平原");
            Toggle("job-series-move-cost", "平原");
            Toggle("job-series-attributes", "兵种系 0");
            Toggle("detailed-job-growth", "攻击成长");
            Toggle("detailed-job-growth", "移动力成长");
            Toggle("detailed-job-growth", "攻击范围");
            Toggle("detailed-job-growth", "普通剑");
            Toggle("detailed-job-penetration", "穿透");
            Toggle("strategies", "兵种00学习等级");
            Toggle("items-low", "初始能力");
            Toggle("items-low", "能力成长");
            Toggle("items-low", "价格（/100）");
            Toggle("items-low", "装备特效值");
            Toggle("items-low", "宝物图鉴");
            Set("item-description-low", 0, "宝物说明", "宝物介绍跨表写回测试");
            Toggle("strategy-damage-type", "内容");
            Toggle("strategy-damage-ratio", "内容");
            Toggle("strategy-battle-ai", "内容");
            Toggle("strategy-training-ai", "内容");
            Toggle("strategy-learning", "内容");
            Toggle("strategy-extension", "内容");
            Toggle("strategy-terrain-availability", "策略施展地形掩码");
            Set("scenario-names", 0, "战役名称", "商店跨表写回测试");
            Set("shops", 0, "开关仓库人物", 65535);
            Set("shops", 0, "买卖物品人物", 65535);
            var shop = workspace.GetDocument("shops");
            var goods = shop.Definition.Fields.Where(field => field.ReferenceTable == "items").ToArray();
            var item = workspace.GetDocument("items-low").Data.Rows.Cast<DataRow>()
                .First(row => Convert.ToInt32(row["ID"], CultureInfo.InvariantCulture) != Convert.ToInt32(shop.Data.Rows[0][goods[0].Name], CultureInfo.InvariantCulture) &&
                    IsDefinedItem(Convert.ToString(row["名称"], CultureInfo.InvariantCulture)));
            Set("shops", 0, goods[0].Name, Convert.ToInt32(item["ID"], CultureInfo.InvariantCulture));
            Set("shops", 0, goods[1].Name, 255);

            var applied = transaction.Apply();
            TestSupport.Assert(applied.Applied && applied.ChangedCells >= 24 && workspace.IsDirty, "业务跨模块事务没有形成完整草稿");
            workspace.Commands.Undo();
            TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo, "业务跨模块事务必须一次整体撤回");
            workspace.Commands.Redo();
            var save = workspace.Save();
            TestSupport.Assert(save.Saved && save.History?.Files.Count == 3,
                "业务跨模块保存应一次提交 Data/EXE/Imsg 三个文件：" + string.Join("；", save.Messages));
            var after = before.Keys.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
            var reread = new ProjectWorkspace();
            reread.Open(root, "ccz-6.5", "65-standard-v1");
            foreach (var (key, row, field, value) in expected)
                TestSupport.Assert(Convert.ToString(reread.GetDocument(key).Data.Rows[row][field], CultureInfo.InvariantCulture) == Convert.ToString(value, CultureInfo.InvariantCulture),
                    $"业务表保存后复读不一致：{key}[{row}].{field}");
            foreach (var (name, bytes) in before)
            {
                var allowed = expected.Where(edit => Path.GetFileName(workspace.GetDocument(edit.Key).FilePath) == name)
                    .Select(edit =>
                    {
                        var document = workspace.GetDocument(edit.Key);
                        var field = document.Definition.Fields.Single(field => field.Name == edit.Field);
                        var start = document.Definition.DataOffset + (long)edit.Row * document.Definition.RowSize + field.Offset;
                        return (Start: start, End: start + field.Size);
                    }).ToArray();
                TestSupport.Assert(Enumerable.Range(0, bytes.Length).Where(index => bytes[index] != after[name][index])
                    .All(index => allowed.Any(range => index >= range.Start && index < range.End)), "业务写回修改了目标字段以外的原始字节：" + name);
            }
            TestSupport.Assert(reread.UndoSaved().Success && before.All(file => File.ReadAllBytes(Path.Combine(root, file.Key)).SequenceEqual(file.Value)),
                "业务跨文件已保存历史没有整体撤回");
            TestSupport.Assert(reread.RedoSaved().Success && after.All(file => File.ReadAllBytes(Path.Combine(root, file.Key)).SequenceEqual(file.Value)),
                "业务跨文件已保存历史没有整体重做");
            TestShopPlaceholderRejection(reread);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestShopPlaceholderRejection(ProjectWorkspace workspace)
    {
        var pages = new BusinessDataService(workspace);
        var items = workspace.GetDocument("items-low");
        var shop = workspace.GetDocument("shops");
        var slot = shop.Definition.Fields.First(field => field.ReferenceTable == "items").Name;
        var current = Convert.ToInt32(shop.Data.Rows[0][slot], CultureInfo.InvariantCulture);
        var rows = items.Data.Rows.Cast<DataRow>().Select((row, index) => (Row: row, Index: index))
            .Where(item => Convert.ToInt32(item.Row["ID"], CultureInfo.InvariantCulture) != current).Take(2).ToArray();
        var setup = pages.BeginTransaction(BusinessPageKind.Items, "测试空物品与占位物品");
        setup.Set("items-low", rows[0].Index, "名称", string.Empty).Set("items-low", rows[1].Index, "名称", "空闲").Apply();
        var previousName = workspace.GetDocument("scenario-names").Data.Rows[0]["战役名称"];
        foreach (var invalidId in rows.Select(item => Convert.ToInt32(item.Row["ID"], CultureInfo.InvariantCulture)))
        {
            var rejected = false;
            try
            {
                pages.BeginTransaction(BusinessPageKind.Shop, "非法商品不得产生部分草稿")
                    .Set("scenario-names", 0, "战役名称", "不应写入")
                    .Set("shops", 0, slot, invalidId).Apply();
            }
            catch (InvalidDataException ex) { rejected = ex.Message.Contains("占位物品", StringComparison.Ordinal); }
            TestSupport.Assert(rejected && Equals(workspace.GetDocument("scenario-names").Data.Rows[0]["战役名称"], previousName) &&
                Convert.ToInt32(shop.Data.Rows[0][slot], CultureInfo.InvariantCulture) == current,
                "空/占位物品未被原子拒绝，或已产生部分商店草稿");
        }
        workspace.Commands.Undo();
        TestSupport.Assert(!workspace.IsDirty && !workspace.Commands.CanUndo, "被拒绝的商店事务不应增加撤回步骤");
        workspace.ValidateCellValue(shop, 0, "开关仓库人物", 65535);
        workspace.ValidateCellValue(shop, 0, slot, 255);
    }

    private static bool IsDefinedItem(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Trim() is not ("空闲" or "空槽" or "未使用" or "保留" or "FF-End" or "End");
}
