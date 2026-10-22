using DataManager.Core.Editing;
using DataManager.Core.Models;

namespace DataManager.Tests;

internal static class ScanMatrixTests
{
    private static readonly (string Directory, string Variant)[] Samples =
    [
        ("曹操传6.6-V7.16 解压密码：66", "66-standard-v1"),
        ("新改曹操傳6.6修正版", "66-standard-v1"),
        ("清儿吕布传", "66-standard-v1"),
        ("烈焰同盟6.6x(新修版)黄线完结+红线5关", "66-standard-v1"),
        ("复刻版6.6山贼", "66-standard-v1"),
        ("复刻版6.6山贼 国庆", "66-standard-v1"),
        ("厚黑6.6测（7.9水杯版）", "66-legacy-data-v1")
    ];

    public static void Run(string basePath)
    {
        var baseRoot = Path.Combine(TestSupport.RepositoryRootFromBase(basePath), "基底");
        foreach (var sample in Samples)
        {
            var path = Path.Combine(baseRoot, sample.Directory);
            if (!Directory.Exists(path)) continue;
            var workspace = new ProjectWorkspace();
            var report = workspace.Open(path);
            TestSupport.Assert(workspace.ActiveProfile.ProfileId == "ccz-6.6", $"{sample.Directory} 未识别为 6.6");
            TestSupport.Assert(workspace.ActiveProfile.VariantId == sample.Variant, $"{sample.Directory} 布局识别错误：{workspace.ActiveProfile.VariantId}");
            TestSupport.Assert(report.ProfileMatch?.MatchKind is ProfileMatchKind.Exact or ProfileMatchKind.Compatible or ProfileMatchKind.Partial, $"{sample.Directory} 未达到可用的 Profile/Variant 匹配");
            TestSupport.Assert(workspace.Documents.ContainsKey("persons") && workspace.Documents.ContainsKey("strategies"), $"{sample.Directory} 核心表读取不完整：人物/策略缺失");
            TestSupport.Assert(workspace.GetDocument("persons").Data.Columns.Contains("兵种"), $"{sample.Directory} 人物兵种字段缺失");
            TestSupport.Assert(!workspace.GetDocument("persons").Data.Columns.Contains("级别"), $"{sample.Directory} 暴露了人物级别");
            var icon = workspace.GetDocument("strategies").Definition.Fields.Single(field => field.Name == "策略图标");
            TestSupport.Assert(workspace.GetDocument("strategies").Data.Columns["策略图标"]?.DataType == typeof(int) &&
                icon.Kind == FieldKind.UInt8 && icon.Offset == 0x10 && icon.Size == 1 && icon.Policy == FieldPolicy.Editable,
                $"{sample.Directory} 策略图标未按已验证的+0x10字节字段读取");
            TestSupport.Assert(!workspace.Documents.ContainsKey("item-effect-names-low") && !workspace.Documents.ContainsKey("item-effect-names-high"),
                $"{sample.Directory} 错误读取了 6.5 变长装备特效名称池");
        }
    }
}
