using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DataManager.App;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

internal static class EngineEffectUiTests
{
    public static void Run(string sourceRoot, string? outputDirectory = null)
    {
        var copy = TestSupport.CreateCoreCopy(sourceRoot, restoreKnown65Snapshot: false);
        try
        {
            using var form = new MainForm(openDefaultProject: false)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-30000, -30000),
                ShowInTaskbar = false
            };
            form.Show();
            form.LoadProjectForSmokeTest(copy);
            form.SelectModuleForSmokeTest("__engine");
            var scanDeadline = DateTime.UtcNow.AddSeconds(60);
            do { Pump(); } while (form.EngineScanPendingForSmokeTest && DateTime.UtcNow < scanDeadline);
            Pump();
            TestSupport.Assert(!form.EngineScanPendingForSmokeTest, "后台引擎扫描没有及时完成。");
            var list = form.EntityListForSmokeTest;
            TestSupport.Assert(list.Items.Count > 0, "引擎扫描列表为空。");
            TestSupport.Assert(list.Items[0]!.ToString()!.Contains('：'), "左侧列表不是 ID：名称 格式。");
            TestSupport.Assert(!form.PhysicalGridForSmokeTest.Visible, "引擎扫描页仍显示旧表格。");
            var detail = form.EngineDetailForSmokeTest;
            var version = form.WorkspaceForSmokeTest.ActiveProfile.Profile.Version;
            TestSupport.Assert(detail.Visible, "引擎详情面板未显示。");
            TestSupport.Assert(detail.TitleForSmokeTest.Contains('：'), "详情标题缺少 ID：名称。");
            TestSupport.Assert(detail.SuffixForSmokeTest.Contains("0x", StringComparison.Ordinal), "详情缺少后缀。");
            TestSupport.Assert(detail.CallRowCountForSmokeTest >= 1, "首个特效没有调用点行。");
            TestSupport.Assert(detail.CallsGridForSmokeTest.Columns.Count == 9, "调用点表格列数不正确。");
            TestSupport.Assert(detail.CallsTabsForSmokeTest.TabPages.Count == 7, "调用点页签数量不正确。");
            TestSupport.Assert(detail.UnassignedGridForSmokeTest.Rows.Count > 0, "未归属调用页签为空。");
            if (version == "6.4")
                TestSupport.Assert(detail.InternalGridForSmokeTest.Rows.Count == 0,
                    "6.4 的寄存器聚合器不再按四参数入口统计，内部调用页签应为空。");
            else
                TestSupport.Assert(detail.InternalGridForSmokeTest.Rows.Count > 0, "引擎内部页签为空。");
            TestSupport.Assert(detail.DerivedGridForSmokeTest.Rows.Count > 0, "派生调用页签为空。");
            TestSupport.Assert(detail.CallsTabsForSmokeTest.TabPages[4].Text.StartsWith("跨文件引用（", StringComparison.Ordinal),
                "跨文件引用页签标题不正确。");
            TestSupport.Assert(detail.ReferenceGridForSmokeTest.Columns.Count == 6, "跨文件引用表格列数不正确。");
            TestSupport.Assert(detail.ReferenceSummaryForSmokeTest.Length > 0, "跨文件引用摘要为空。");
            TestSupport.Assert(detail.ReferenceSummaryForSmokeTest.Contains("成长拓展", StringComparison.Ordinal) ||
                              detail.ReferenceSummaryForSmokeTest.Contains("未加载 Effectmsg", StringComparison.Ordinal),
                "跨文件引用摘要缺少成长拓展状态。");
            TestSupport.Assert(detail.ReferenceSummaryForSmokeTest.Contains("静态分配表行", StringComparison.Ordinal),
                "跨文件引用摘要缺少静态分配表行（特效 0 的表行非空）。");
            TestSupport.Assert(detail.CallsTabsForSmokeTest.TabPages[5].Text.StartsWith("调用链（", StringComparison.Ordinal),
                "调用链页签标题不正确。");
            TestSupport.Assert(detail.CallPathsGridForSmokeTest.Columns.Count == 5, "调用链表格列数不正确。");
            TestSupport.Assert(detail.CallPathsGridForSmokeTest.Rows.Count > 0, "调用链页签没有路径行。");
            TestSupport.Assert(detail.CallPathsGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                    .Any(row => row.Cells[3].Value?.ToString() == "到 E8 根"),
                "调用链页签没有到达 E8 根的路径。");
            TestSupport.Assert(detail.CallPathsGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                    .Any(row => row.Cells[2].Value?.ToString()?.Contains("（PE 入口）", StringComparison.Ordinal) == true),
                "调用链页签没有标注 PE 入口根。");
            TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("调用点", StringComparison.Ordinal),
                "反汇编证据面板没有初始化。");
            TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("E8 根类型", StringComparison.Ordinal),
                "反汇编证据面板没有根类型说明。");
            TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("函数角色", StringComparison.Ordinal),
                "反汇编证据面板没有函数角色说明。");
            TestSupport.Assert(form.StatusForSmokeTest.Contains("内部", StringComparison.Ordinal), "状态栏缺少内部调用摘要。");
            TestSupport.Assert(form.StatusForSmokeTest.Contains("未对齐 E8", StringComparison.Ordinal),
                "状态栏缺少未对齐 E8 摘要。");
            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "引擎页产生了数据草稿。");
            var smokeReport = form.EngineReportForSmokeTest;
            TestSupport.Assert(smokeReport?.AssignmentTable is { Verified: true },
                "引擎扫描没有解析静态分配表几何。");
            var covered = smokeReport!.Effects.FirstOrDefault(row => !row.HasCalls && row.AssignmentCovered);
            if (covered != null)
            {
                form.SelectEngineEffectForSmokeTest(covered.EffectId);
                Pump();
                TestSupport.Assert(detail.CallEmptyVisibleForSmokeTest &&
                                   detail.CallEmptyTextForSmokeTest.Contains("静态分配表行", StringComparison.Ordinal),
                    $"无调用特效 {covered.EffectId} 没有显示静态分配表行说明。");
                TestSupport.Assert(detail.CallEmptyTextForSmokeTest.Contains("通用分派链", StringComparison.Ordinal),
                    "无调用特效说明缺少通用分派链。");
                TestSupport.Assert(detail.CallEmptyTextForSmokeTest.Contains(
                        $"0x{covered.AssignmentRowOffset:X}", StringComparison.OrdinalIgnoreCase),
                    "静态分配表行说明缺少文件偏移。");
            }

            var total = list.Items.Count;
            var filter = form.EngineOnlyWithCallsForSmokeTest;
            filter.Checked = true;
            Pump();
            var filtered = list.Items.Count;
            TestSupport.Assert(filtered > 0 && filtered < total, $"筛选后数量异常：{filtered}/{total}");
            TestSupport.Assert(list.Items.Cast<object>().Any(item => item.ToString()!.StartsWith("0：", StringComparison.Ordinal)),
                "筛选后缺少 ID 0。");
            var derivedOnlyPrefix = version switch { "6.4" => "61：", "6.5" => "210：", "6.6" => "171：", _ => null };
            if (derivedOnlyPrefix != null)
                TestSupport.Assert(list.Items.Cast<object>().Any(item =>
                        item.ToString()!.StartsWith(derivedOnlyPrefix, StringComparison.Ordinal)),
                    $"筛选后缺少仅派生特效 {derivedOnlyPrefix}。");
            filter.Checked = false;
            Pump();
            TestSupport.Assert(list.Items.Count == total, "取消筛选没有恢复列表。");

            list.SelectedIndex = 0;
            Pump();
            detail.SelectCallForSmokeTest(0);
            Pump();
            var mode = detail.ValueModeComboForSmokeTest;
            var originalMode = mode.SelectedIndex;
            mode.SelectedIndex = originalMode == 0 ? 1 : 0;
            Pump();
            TestSupport.Assert(detail.HasPendingChanges, "修改效果值模式后没有待写入状态。");
            TestSupport.Assert(detail.WriteButtonForSmokeTest.Enabled, "写入按钮未启用。");
            TestSupport.Assert(detail.TitleForSmokeTest.EndsWith('*'), "标题没有待写入标记。");
            detail.DiscardPendingChanges();
            Pump();
            TestSupport.Assert(!detail.HasPendingChanges, "放弃修改后仍有待写入状态。");

            detail.NameEditorForSmokeTest.Editor.Text = "测试名称";
            Pump();
            TestSupport.Assert(detail.HasPendingChanges, "名称修改没有进入待写入状态。");
            detail.DiscardPendingChanges();
            Pump();

            detail.DescriptionEditorForSmokeTest.Editor.Text = "测试说明";
            Pump();
            TestSupport.Assert(detail.HasPendingChanges, "说明修改没有进入待写入状态。");
            detail.DiscardPendingChanges();
            Pump();

            var suffix = detail.SuffixComboForSmokeTest;
            TestSupport.Assert(suffix.Items.Count >= EngineEffectSuffixLabels.MaximumSuffix(version) + 1,
                $"{version} 后缀下拉未按基底生成：{suffix.Items.Count}");
            if (suffix.Items.Count > 1)
            {
                suffix.SelectedIndex = suffix.SelectedIndex == 0 ? 1 : 0;
                Pump();
                TestSupport.Assert(detail.HasPendingChanges, "后缀修改没有进入待写入状态。");
                detail.DiscardPendingChanges();
                Pump();
            }

            var rawSuffixEffect = version switch { "6.5" => 254, "6.6" => 108, _ => -1 };
            if (rawSuffixEffect >= 0)
            {
                var rawIndex = FindEffectIndex(list, rawSuffixEffect);
                TestSupport.Assert(rawIndex >= 0, $"{version} 左侧列表缺少特效 {rawSuffixEffect}。");
                var currentIndex = list.SelectedIndex;
                list.SelectedIndex = rawIndex;
                Pump();
                var rawText = version == "6.5" ? "0x64" : "0x12";
                TestSupport.Assert(detail.SuffixComboForSmokeTest.Items.Cast<object>()
                        .Any(item => item.ToString()?.Contains(rawText, StringComparison.Ordinal) == true),
                    $"{version} 特效 {rawSuffixEffect} 的后缀下拉缺少 {rawText} 原始值项。");
                list.SelectedIndex = currentIndex >= 0 ? currentIndex : 0;
                Pump();
            }

            TestSupport.Assert(!form.WorkspaceForSmokeTest.IsDirty, "界面编辑产生了数据草稿。");
            TestSupport.Assert(!detail.HasPendingChanges, "测试结束仍有引擎待写入状态。");

            if (version is "6.5" or "6.6")
            {
                var deadIndex = Enumerable.Range(0, list.Items.Count)
                    .FirstOrDefault(index => list.Items[index]?.ToString()?.StartsWith("52：", StringComparison.Ordinal) == true, -1);
                TestSupport.Assert(deadIndex >= 0, $"{version} 左侧列表缺少特效 52。");
                list.SelectedIndex = deadIndex;
                Pump();
                TestSupport.Assert(detail.SelectCallByAddress(0x00405CEE), $"{version} 特效 52 缺少死代码桩调用 0x00405CEE。");
                Pump();
                TestSupport.Assert(detail.CallPathsGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                        .Any(row => row.Cells[2].Value?.ToString()?.Contains("死代码特效桩", StringComparison.Ordinal) == true),
                    $"{version} 调用链根列没有标注死代码特效桩。");
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("死代码依据", StringComparison.Ordinal),
                    $"{version} 证据面板没有死代码依据。");
                list.SelectedIndex = 0;
                Pump();
            }

            var tabs = detail.CallsTabsForSmokeTest;
            tabs.SelectedTab = tabs.TabPages[1];
            Pump();
            TestSupport.Assert(detail.UnassignedGridForSmokeTest.Rows.Count > 0, "未归属调用页签没有行。");
            var unassignedAddress = version == "6.6" ? 0x004039D5u : 0x004039D6u;
            TestSupport.Assert(detail.SelectCallByAddress(unassignedAddress), "未归属调用页签没有目标地址。");
            Pump();
            TestSupport.Assert(detail.CurrentCallAddress == unassignedAddress, "未归属调用没有绑定到当前调用点。");
            TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("movzx", StringComparison.OrdinalIgnoreCase),
                "未归属调用的证据缺少 movzx 定义链。");
            var branchEvidence = detail.EvidenceBoxForSmokeTest.Text;
            TestSupport.Assert(!branchEvidence.Contains("判定路径", StringComparison.Ordinal) ||
                               branchEvidence.Contains("顺序执行", StringComparison.Ordinal) ||
                               branchEvidence.Contains("条件来源：", StringComparison.Ordinal),
                "未归属调用的判定路径没有给出条件操作数来源。");
            if (branchEvidence.Contains("条件来源：", StringComparison.Ordinal))
                TestSupport.Assert(branchEvidence.Contains("→", StringComparison.Ordinal) &&
                                   branchEvidence.Contains("[", StringComparison.Ordinal),
                    "条件来源缺少比较指令或内存/字段操作数。");
            TestSupport.Assert(!detail.NameEditorForSmokeTest.Enabled, "未归属调用选中时名称不应可编辑。");
            TestSupport.Assert(detail.WriteCallButtonForSmokeTest.Enabled, "未归属调用写入按钮未启用。");
            if (version == "6.4")
            {
                TestSupport.Assert(detail.SelectCallByAddress(0x0043D037), "6.4 安全计算立即数调用不存在。");
                Pump();
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("mov cl,66h", StringComparison.OrdinalIgnoreCase),
                    "6.4 安全计算立即数证据缺少 mov cl,0x66。");
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("可写（定义 0x0043D02C）",
                    StringComparison.Ordinal), "6.4 安全计算立即数没有显示可写定义。");
                var caveIndex = Enumerable.Range(0, list.Items.Count)
                    .FirstOrDefault(index => list.Items[index]?.ToString()?.StartsWith("159：", StringComparison.Ordinal) == true, -1);
                TestSupport.Assert(caveIndex >= 0, "6.4 左侧列表缺少特效 159。");
                list.SelectedIndex = caveIndex;
                Pump();
                TestSupport.Assert(detail.SelectCallByAddress(0x004D0023), "6.4 代码洞特效调用不存在。");
                Pump();
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("跳转来源 0x004CFFFD",
                    StringComparison.Ordinal), "6.4 代码洞调用证据缺少跳转来源。");
                TestSupport.Assert(detail.PersonalIdComboForSmokeTest.Enabled, "6.4 代码洞个人号不可编辑。");
                TestSupport.Assert(detail.SelectCallByAddress(0x0041A447), "6.4 代码洞调用 0x0041A447 不存在。");
                Pump();
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("代码洞 0x0041A430", StringComparison.Ordinal) &&
                                  detail.EvidenceBoxForSmokeTest.Text.Contains("0x00407D11", StringComparison.Ordinal),
                    "6.4 0x0041A447 证据没有标记由 0x00407D11 跳转进入代码洞 0x0041A430。");
            }
            if (detail.InternalGridForSmokeTest.Rows.Count > 0)
            {
                tabs.SelectedTab = tabs.TabPages[2];
                Pump();
                var internalCall = detail.InternalGridForSmokeTest.Rows[0].Tag as EngineEffectCallSite;
                TestSupport.Assert(internalCall != null, "引擎内部调用缺少 Tag。");
                TestSupport.Assert(detail.SelectCallByAddress(internalCall!.CallAddress), "引擎内部调用定位失败。");
                Pump();
                TestSupport.Assert(detail.CurrentCallAddress == internalCall.CallAddress, "引擎内部调用没有绑定。");
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("引擎内部", StringComparison.Ordinal),
                    "引擎内部调用的证据缺少内部原因。");
            }
            if (detail.DerivedGridForSmokeTest.Rows.Count > 0)
            {
                tabs.SelectedTab = tabs.TabPages[3];
                Pump();
                var derivedCall = detail.DerivedGridForSmokeTest.Rows[0].Tag as EngineEffectCallSite;
                TestSupport.Assert(derivedCall != null, "派生调用缺少 Tag。");
                TestSupport.Assert(detail.SelectCallByAddress(derivedCall!.CallAddress), "派生调用定位失败。");
                Pump();
                TestSupport.Assert(detail.CurrentCallAddress == derivedCall.CallAddress, "派生调用没有绑定。");
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("派生调用", StringComparison.Ordinal),
                    "派生调用的证据缺少派生说明。");
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("参数映射", StringComparison.Ordinal),
                    "派生调用的证据缺少参数映射。");
                var tableRow = detail.DerivedGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => row.Tag is EngineEffectCallSite call &&
                                           call.Pattern == EngineEffectCallPattern.TableDerived);
                TestSupport.Assert(tableRow != null, "派生/表驱动页签没有表驱动行。");
                detail.SelectDerivedRowForSmokeTest(tableRow!.Index);
                Pump();
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("表驱动调用", StringComparison.Ordinal),
                    "表驱动调用的证据缺少表驱动说明。");
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("写入约束", StringComparison.Ordinal),
                    "表驱动调用的证据缺少写入约束。");
                TestSupport.Assert(detail.ItemIdComboForSmokeTest.Enabled, "表驱动装备号不可编辑。");
                TestSupport.Assert(!detail.PersonalIdComboForSmokeTest.Enabled, "表驱动个人号不应可编辑。");
                var originalItem = detail.ItemIdForSmokeTest ?? 0;
                detail.ItemIdForSmokeTest = originalItem == 0 ? 1 : originalItem - 1;
                Pump();
                TestSupport.Assert(detail.HasPendingChanges, "表项修改没有进入待写入状态。");
                detail.DiscardPendingChanges();
                Pump();
                TestSupport.Assert(!detail.HasPendingChanges, "放弃表项修改后仍有待写入状态。");
                var loopRow = detail.DerivedGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => row.Tag is EngineEffectCallSite call &&
                                           call.Pattern is EngineEffectCallPattern.LoopDerived
                                               or EngineEffectCallPattern.NestedDerived);
                TestSupport.Assert(loopRow != null, "派生/表驱动页签没有循环/二级包装行。");
                detail.SelectDerivedRowForSmokeTest(loopRow!.Index);
                Pump();
                TestSupport.Assert(detail.EvidenceBoxForSmokeTest.Text.Contains("展开", StringComparison.Ordinal),
                    "循环/二级包装的证据缺少展开说明。");
                TestSupport.Assert(!detail.ItemIdComboForSmokeTest.Enabled &&
                                  !detail.PersonalIdComboForSmokeTest.Enabled,
                    "循环/二级包装参数不应可编辑。");
            }
            tabs.SelectedTab = tabs.TabPages[0];
            Pump();
            detail.SelectCallForSmokeTest(0);
            Pump();
            TestSupport.Assert(detail.NameEditorForSmokeTest.Enabled, "回到效果调用后名称没有恢复编辑。");

            form.SelectModuleForSmokeTest("__engine");
            var engineTabs = form.EngineSubTabsForSmokeTest;
            engineTabs.SelectedIndex = 4;
            Pump();
            var constants = form.ConstantsForSmokeTest;
            TestSupport.Assert(constants.Visible, "常量页未显示。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Count == EngineConstantService.Definitions.Count + 5,
                "常量页条目数量不正确。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "引导攻击追加次数"), "常量页缺少引导攻击次数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "突破攻击台词"), "常量页缺少突破攻击台词。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "突破攻击伤害加成"), "常量页缺少突破攻击伤害加成。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "吸血攻击吸取上限"), "常量页缺少吸血攻击吸取上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "吸收金钱获得倍率"), "常量页缺少吸收金钱获得倍率。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "绝对伤害触发 HP 线"), "常量页缺少绝对伤害触发 HP 线。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "绝对伤害追加比例"), "常量页缺少绝对伤害追加比例。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "风驰电掣伤害除数"), "常量页缺少风驰电掣伤害除数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "化力为疗每点伤害MP消耗"), "常量页缺少化力为疗MP消耗。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "夺锐攻击能力上限"), "常量页缺少夺锐攻击能力上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "商店卖出价格比例分子"), "常量页缺少商店卖出价格比例分子。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "铁索横江邻接方向数"), "常量页缺少铁索横江邻接方向数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "猛击伤害比例上限"), "常量页缺少猛击伤害比例上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "盾反反弹比例上限"), "常量页缺少盾反反弹比例上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "神魔附体属性上限"), "常量页缺少神魔附体属性上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "疾风攻击每点增伤"), "常量页缺少疾风攻击每点增伤。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "疾风攻击补正值（6.6）"), "常量页缺少疾风攻击补正值。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "奋战攻击目标数"), "常量页缺少奋战攻击目标数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "穿越移动判定方向数"), "常量页缺少穿越移动判定方向数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "物理攻击基础伤害"), "常量页缺少物理攻击基础伤害。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "策略攻击基础伤害"), "常量页缺少策略攻击基础伤害。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "策略伤害能力差除数"), "常量页缺少策略伤害能力差除数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "绝对策略伤害追加比例"), "常量页缺少绝对策略伤害追加比例。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "限制策略伤害额外减伤"), "常量页缺少限制策略伤害额外减伤。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "孤勇者减伤上限"), "常量页缺少孤勇者减伤上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "连续攻击减伤封顶"), "常量页缺少连续攻击减伤封顶。");
            constants.SelectRowForSmokeTest(0);
            var editableHint = constants.EditorHintForSmokeTest;
            TestSupport.Assert(editableHint.Contains("可改", StringComparison.Ordinal) &&
                !editableHint.Contains("证据：", StringComparison.Ordinal),
                "常量编辑提示不够简洁：" + editableHint);
            var readOnlyRow = constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .FirstOrDefault(row => row.Tag is EngineConstantValue value && !value.SignatureMatches);
            if (readOnlyRow != null)
            {
                constants.SelectRowForSmokeTest(readOnlyRow.Index);
                var readOnlyHint = constants.EditorHintForSmokeTest;
                TestSupport.Assert(readOnlyHint.StartsWith("只读：", StringComparison.Ordinal) &&
                    !readOnlyHint.Contains("指令签名", StringComparison.Ordinal),
                    "常量只读提示不够简洁：" + readOnlyHint);
            }
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "越战越勇伤害除数"), "常量页缺少越战越勇伤害除数。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "进退有据翻倍触发 HP 线"), "常量页缺少进退有据翻倍触发 HP 线。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "策略命中/格挡率上限"), "常量页缺少策略命中/格挡率上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "逆乱阴阳命中加成"), "常量页缺少逆乱阴阳命中加成。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "攻守兼备减伤效果值上限"), "常量页缺少攻守兼备减伤效果值上限。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "趁虚攻击虚弱阈值"), "常量页缺少趁虚攻击虚弱阈值。");
            TestSupport.Assert(constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .Any(row => row.Cells[1].Value?.ToString() == "神锋沥辉保底加成"), "常量页缺少神锋沥辉保底加成。");
            TestSupport.Assert(constants.GridForSmokeTest.Columns.Count == 6 &&
                constants.GridForSmokeTest.Columns[5].HeaderText == "性质", "常量页缺少性质列。");
            var natureSwift = constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[1].Value?.ToString() == "疾风攻击补正值（6.6）");
            TestSupport.Assert(natureSwift.Cells[5].Value?.ToString() == "代码固定值", "疾风补正的性质不是代码固定值。");
            var natureSwiftBonus = constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[1].Value?.ToString() == "疾风攻击每点增伤");
            TestSupport.Assert(natureSwiftBonus.Cells[5].Value?.ToString() ==
                (version == "6.6" ? "特效值槽" : "代码固定值"),
                $"疾风攻击每点增伤性质与基底不符：{natureSwiftBonus.Cells[5].Value}（{version}）");
            var natureQuote = constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[1].Value?.ToString() == "突破攻击台词");
            TestSupport.Assert(natureQuote.Cells[5].Value?.ToString() == "文本槽", "突破台词的性质不是文本槽。");
            constants.SelectRowForSmokeTest(0);
            Pump();
            TestSupport.Assert(constants.NumberForSmokeTest.Enabled, "引导攻击次数不可编辑。");
            var quoteRow = constants.GridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[1].Value?.ToString() == "突破攻击台词").Index;
            constants.SelectRowForSmokeTest(quoteRow);
            Pump();
            TestSupport.Assert(constants.TextForSmokeTest.Enabled && constants.TextForSmokeTest.Text.Length > 0,
                "突破攻击台词输入框未显示当前台词。");
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
                {
                    form.Size = size;
                    if (form.Size != size)
                        SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                    form.PerformLayout();
                    Pump(10);
                    using var constantBitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(constantBitmap, new Rectangle(Point.Empty, constantBitmap.Size));
                    constantBitmap.Save(Path.Combine(outputDirectory,
                        $"常量-子页-{version}-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                }
            }
            Console.WriteLine($"ENGINE_CONSTANT_UI_OK rows={constants.GridForSmokeTest.Rows.Count}");

            form.SelectModuleForSmokeTest("__formulas");
            Pump();
            var battle = form.BattleFormulasForSmokeTest;
            TestSupport.Assert(battle.Visible, "公式设定页未显示。");
            TestSupport.Assert(form.EntityListForSmokeTest.Visible, "公式设定页没有显示左侧列表。");
            TestSupport.Assert(form.EntityListForSmokeTest.Items.Count == EngineBattleFormulaService.Definitions.Count,
                "公式列表条目数与定义不符。");
            TestSupport.Assert(battle.ValueForSmokeTest?.Definition.Id == "hit-curve",
                "公式页没有默认选中命中率曲线。");
            TestSupport.Assert(battle.StepGridForSmokeTest.Rows.Count >= 3, "公式步骤表过少。");
            TestSupport.Assert(battle.ConstantGridForSmokeTest.Columns.Count == 8,
                "公式常量表格列数不正确。");

            form.SelectFormulaForSmokeTest("physical-damage");
            Pump();
            var currentColumn = version == "6.4" ? 2 : version == "6.5" ? 3 : 4;
            var physicalBaseRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "物理攻击基础伤害");
            TestSupport.Assert(physicalBaseRow.Cells[currentColumn].Value?.ToString() == "25",
                $"物理攻击基础伤害当前值不正确：{physicalBaseRow.Cells[currentColumn].Value}");
            TestSupport.Assert(physicalBaseRow.Cells[7].Value?.ToString() == "可改",
                "物理攻击基础伤害没有标记为可改。");

            form.SelectFormulaForSmokeTest("hit-curve");
            Pump();
            var hitCap = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "命中上限");
            TestSupport.Assert(hitCap.Cells[currentColumn].Value?.ToString() == "100" &&
                hitCap.Cells[7].Value?.ToString() == "可改",
                "命中率上限没有显示为可改。");
            TestSupport.Assert(battle.SetPendingForSmokeTest("hit-curve-cap", 90),
                "命中率上限无法进入待写入状态。");
            TestSupport.Assert(battle.HasPendingChanges && battle.WriteButtonForSmokeTest.Enabled,
                "待写入状态没有启用写入按钮。");
            battle.DiscardPendingChanges();
            TestSupport.Assert(!battle.HasPendingChanges, "放弃修改后仍有待写入。");

            form.SelectFormulaForSmokeTest("counter-damage");
            Pump();
            var counterRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "普通反击伤害份数");
            TestSupport.Assert(version == "6.4"
                    ? counterRow.Cells[currentColumn].Value?.ToString() == "—" &&
                      counterRow.Cells[7].Value?.ToString() == "不适用此基底"
                    : counterRow.Cells[currentColumn].Value?.ToString() == "3" &&
                      counterRow.Cells[7].Value?.ToString() == "可改",
                $"普通反击份数与基底不符：{counterRow.Cells[currentColumn].Value}（{version}）");

            form.SelectFormulaForSmokeTest("hit-curve");
            Pump();
            var floorRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "6.6 命中下限");
            TestSupport.Assert(version == "6.6"
                    ? floorRow.Cells[currentColumn].Value?.ToString() == "25" &&
                      (floorRow.Cells[7].Value?.ToString() == "可改" ||
                       floorRow.Cells[7].Value?.ToString() == "可改（共用）")
                    : floorRow.Cells[currentColumn].Value?.ToString() == "—" &&
                      floorRow.Cells[7].Value?.ToString() == "不适用此基底",
                $"6.6 命中下限与基底不符：{floorRow.Cells[currentColumn].Value}（{version}）");

            form.SelectFormulaForSmokeTest("personal-exp");
            Pump();
            var personalBaseRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "基础经验值");
            TestSupport.Assert(personalBaseRow.Cells[currentColumn].Value?.ToString() == "6" &&
                (personalBaseRow.Cells[7].Value?.ToString() == "可改" ||
                 personalBaseRow.Cells[7].Value?.ToString() == "可改（共用）"),
                $"个人经验基础值没有显示为可改：{personalBaseRow.Cells[currentColumn].Value}");
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var expSize = new Size(1280, 720);
                form.Size = expSize;
                if (form.Size != expSize)
                    SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, expSize.Width, expSize.Height,
                        0x0010 | 0x0004 | 0x0400);
                form.PerformLayout();
                Pump(10);
                using var expBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(expBitmap, new Rectangle(Point.Empty, expBitmap.Size));
                expBitmap.Save(Path.Combine(outputDirectory,
                    $"公式设定-经验-{version}-1280x720.png"), ImageFormat.Png);
            }

            form.SelectFormulaForSmokeTest("equip-level-exp");
            Pump();
            var equipThresholdRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "普通经验阈值");
            var expectedEquipThreshold = version == "6.5" ? "150" : "100";
            TestSupport.Assert(equipThresholdRow.Cells[currentColumn].Value?.ToString() == expectedEquipThreshold &&
                equipThresholdRow.Cells[7].Value?.ToString() == "可改",
                $"装备普通经验阈值没有显示为可改：{equipThresholdRow.Cells[currentColumn].Value}（{version}）");
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var equipSize = new Size(1280, 720);
                form.Size = equipSize;
                if (form.Size != equipSize)
                    SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, equipSize.Width, equipSize.Height,
                        0x0010 | 0x0004 | 0x0400);
                form.PerformLayout();
                Pump(10);
                using var equipBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(equipBitmap, new Rectangle(Point.Empty, equipBitmap.Size));
                equipBitmap.Save(Path.Combine(outputDirectory,
                    $"公式设定-装备经验-{version}-1280x720.png"), ImageFormat.Png);
            }

            form.SelectFormulaForSmokeTest("merit-record");
            Pump();
            var meritRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "本场战功表");
            TestSupport.Assert(meritRow.Cells[currentColumn].Value?.ToString() == "0x508000" &&
                meritRow.Cells[7].Value?.ToString() != "可改",
                $"本场战功表没有正确显示：{meritRow.Cells[currentColumn].Value}（{version}）");
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var meritSize = new Size(1280, 720);
                form.Size = meritSize;
                if (form.Size != meritSize)
                    SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, meritSize.Width, meritSize.Height,
                        0x0010 | 0x0004 | 0x0400);
                form.PerformLayout();
                Pump(10);
                using var meritBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(meritBitmap, new Rectangle(Point.Empty, meritBitmap.Size));
                meritBitmap.Save(Path.Combine(outputDirectory,
                    $"公式设定-功勋-{version}-1280x720.png"), ImageFormat.Png);
            }

            form.SelectFormulaForSmokeTest("per-turn-exp");
            Pump();
            var selectorRow = battle.ConstantGridForSmokeTest.Rows.Cast<DataGridViewRow>()
                .First(row => row.Cells[0].Value?.ToString() == "选择器表");
            var expectedSelector = version == "6.4" ? "1D 1E 1E"
                : version == "6.5" ? "00 00 00" : "1E 1E 1E";
            TestSupport.Assert(selectorRow.Cells[currentColumn].Value?.ToString() == expectedSelector &&
                selectorRow.Cells[7].Value?.ToString() != "可改",
                $"每回合经验选择器表不符：{selectorRow.Cells[currentColumn].Value}（{version}）");
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                var perTurnSize = new Size(1280, 720);
                form.Size = perTurnSize;
                if (form.Size != perTurnSize)
                    SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, perTurnSize.Width, perTurnSize.Height,
                        0x0010 | 0x0004 | 0x0400);
                form.PerformLayout();
                Pump(10);
                using var perTurnBitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(perTurnBitmap, new Rectangle(Point.Empty, perTurnBitmap.Size));
                perTurnBitmap.Save(Path.Combine(outputDirectory,
                    $"公式设定-每回合经验-{version}-1280x720.png"), ImageFormat.Png);
            }

            form.SelectFormulaForSmokeTest("hit-curve");
            Pump();
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
                {
                    form.Size = size;
                    if (form.Size != size)
                        SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                    form.PerformLayout();
                    Pump(10);
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(outputDirectory,
                        $"公式设定-{version}-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                }
            }
            Console.WriteLine($"ENGINE_BATTLE_FORMULA_UI_OK formulas={EngineBattleFormulaService.Definitions.Count} " +
                $"constants={EngineBattleFormulaService.Definitions.Sum(value => value.Constants.Count)}");
            form.SelectModuleForSmokeTest("__engine");
            engineTabs.SelectedIndex = 0;
            Pump();
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
                foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
                {
                    tabs.SelectedTab = tabs.TabPages[0];
                    detail.SelectCallForSmokeTest(0);
                    form.Size = size;
                    if (form.Size != size)
                        SetWindowPos(form.Handle, IntPtr.Zero, -30000, -30000, size.Width, size.Height, 0x0010 | 0x0004 | 0x0400);
                    form.PerformLayout();
                    Pump(10);
                    Console.WriteLine($"UI_SIZE {version}/{size.Width}x{size.Height}: form={form.ClientSize} detail={detail.Size} dpi={detail.DeviceDpi} rows={detail.TextRowHeightForSmokeTest}/{detail.CallRowHeightForSmokeTest} body={detail.BodySizeForSmokeTest} sections={detail.TextSectionHeightForSmokeTest}/{detail.CallSectionHeightForSmokeTest}/{detail.CallsSectionHeightForSmokeTest} grid={detail.CallsGridForSmokeTest.Height} name={detail.NameEditorForSmokeTest.Height}/{detail.NameEditorForSmokeTest.Editor.Height} desc={detail.DescriptionEditorForSmokeTest.Height}/{detail.DescriptionEditorForSmokeTest.Editor.Height} font={detail.Font.Height}");
                    TestSupport.Assert(detail.NameEditorForSmokeTest.Editor.Height >= detail.NameEditorForSmokeTest.Editor.Font.Height,
                        $"{version}/{size} 名称编辑框被压缩。");
                    TestSupport.Assert(detail.DescriptionEditorForSmokeTest.Editor.Height >= detail.DescriptionEditorForSmokeTest.Editor.Font.Height * 2,
                        $"{version}/{size} 说明编辑框被压缩。");
                    TestSupport.Assert(detail.CallsGridForSmokeTest.Height >= 60, $"{version}/{size} 调用点表格被压缩。");
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(outputDirectory,
                        $"引擎扫描-{version}-{size.Width}x{size.Height}.png"), ImageFormat.Png);

                    detail.SelectCallByAddress(unassignedAddress);
                    tabs.SelectedTab = tabs.TabPages[1];
                    Pump(10);
                    using var unassignedBitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(unassignedBitmap, new Rectangle(Point.Empty, unassignedBitmap.Size));
                    unassignedBitmap.Save(Path.Combine(outputDirectory,
                        $"引擎扫描-{version}-未归属-{size.Width}x{size.Height}.png"), ImageFormat.Png);

                    if (detail.DerivedGridForSmokeTest.Rows.Count > 0)
                    {
                        tabs.SelectedTab = tabs.TabPages[3];
                        detail.SelectCallByAddress(detail.DerivedGridForSmokeTest.Rows[0].Tag is EngineEffectCallSite first
                            ? first.CallAddress : 0);
                        Pump(10);
                        using var derivedBitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(derivedBitmap, new Rectangle(Point.Empty, derivedBitmap.Size));
                        derivedBitmap.Save(Path.Combine(outputDirectory,
                            $"引擎扫描-{version}-派生-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                    }

                    tabs.SelectedTab = tabs.TabPages[4];
                    Pump(10);
                    using var evidenceBitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(evidenceBitmap, new Rectangle(Point.Empty, evidenceBitmap.Size));
                    evidenceBitmap.Save(Path.Combine(outputDirectory,
                        $"引擎扫描-{version}-证据-{size.Width}x{size.Height}.png"), ImageFormat.Png);

                    if (version == "6.4")
                    {
                        detail.SelectCallByAddress(0x0043D037);
                        tabs.SelectedTab = tabs.TabPages[1];
                        Pump(10);
                        using var computedBitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(computedBitmap, new Rectangle(Point.Empty, computedBitmap.Size));
                        computedBitmap.Save(Path.Combine(outputDirectory,
                            $"引擎扫描-6.4-安全计算-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                    }
                }
            }

            form.Close();
            Console.WriteLine("ENGINE_EFFECT_UI_TESTS_OK");
        }
        finally
        {
            Directory.Delete(copy, true);
        }
    }

    private static int FindEffectIndex(ListBox list, int effectId)
    {
        var prefix = effectId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "：";
        for (var index = 0; index < list.Items.Count; index++)
            if (list.Items[index]?.ToString()?.StartsWith(prefix, StringComparison.Ordinal) == true)
                return index;
        return -1;
    }

    private static void Pump(int rounds = 6)
    {
        for (var index = 0; index < rounds; index++)
        {
            TestSupport.PumpUi();
            Thread.Sleep(8);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
