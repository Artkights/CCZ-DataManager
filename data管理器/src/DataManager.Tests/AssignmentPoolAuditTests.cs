using System.Text.Encodings.Web;
using System.Text.Json;
using DataManager.Core.Editing;
using DataManager.Core.Models;
using DataManager.Core.Scanner;

namespace DataManager.Tests;

/// <summary>
/// Test-only diagnostic: dumps why each effect is or is not selectable in the
/// item/job assignment pools. It builds the same catalog the UI uses and never
/// writes to the project.
/// </summary>
internal static class AssignmentPoolAuditTests
{
    public static void Run(string root, string? outputDirectory = null)
    {
        var workspace = new ProjectWorkspace();
        workspace.Open(root);
        var report = new EngineEffectScanService().Scan(workspace);
        var catalog = new EngineEffectAssignmentService().Read(workspace, report);
        var name = Path.GetFileName(root);

        var rows = catalog.Effects.Select(effect => new
        {
            effect.Key,
            effect.Name,
            effect.PersonalEffectId,
            Kind = effect.Kind.ToString(),
            effect.CanBindItem,
            effect.CanBindJob,
            effect.ItemPoolVisible,
            effect.JobPoolVisible,
            effect.ItemSelectors,
            effect.OwnerItemIds,
            effect.FreeJobSlots,
            JobSlotCount = effect.JobSlots.Count,
            effect.ItemReadOnlyReason,
            effect.JobReadOnlyReason,
            effect.ReadOnlyReason,
            effect.ImplementationStatus,
            effect.Source,
            SelectorOwners = catalog.Items.Where(item => item.SelectorFileOffset >= 0 && item.Selector == effect.PersonalEffectId)
                .Select(item => item.Id).ToArray(),
            BindingPointCount = effect.BindingPoints.Count,
            BindingPoints = effect.BindingPoints.Select(call => new
            {
                call.CallAddress,
                call.EntryTarget,
                Pattern = call.Pattern.ToString(),
                call.ItemEffectId,
                call.PersonalEffectId,
                call.ValueMode,
                call.StackingMode,
                ItemWritable = call.ItemPush.WritePlan.IsWritable,
                ItemIsShared = call.ItemPush.WritePlan.IsSharedDefinition,
                ItemDefinitionSource = call.ItemPush.WritePlan.Definition?.Source.ToString(),
                ItemDefinitionText = call.ItemPush.WritePlan.Definition?.Text,
                call.ItemPush.WritePlan.ImmediateFileOffset,
                call.ItemPush.WritePlan.ImmediateLength,
                call.ItemPush.WritePlan.BlockReason,
                ItemResolved = call.ItemPush.ResolvedValue,
                ItemSource = call.ItemPush.SourceKind.ToString()
            }).ToArray()
        }).Cast<object>().ToArray();

        var itemGrey = catalog.Effects.Where(effect => effect.ItemPoolVisible && !effect.CanBindItem).ToArray();
        var jobGrey = catalog.Effects.Where(effect => effect.JobPoolVisible && !effect.CanBindJob).ToArray();
        var summary = new
        {
            Base = name,
            catalog.ExeSha256,
            catalog.JobLayoutVerified,
            catalog.NativeSupported,
            Total = catalog.Effects.Count,
            ItemPoolVisible = catalog.Effects.Count(effect => effect.ItemPoolVisible),
            ItemSelectable = catalog.Effects.Count(effect => effect.ItemPoolVisible && effect.CanBindItem),
            ItemGrey = itemGrey.Length,
            JobPoolVisible = catalog.Effects.Count(effect => effect.JobPoolVisible),
            JobSelectable = catalog.Effects.Count(effect => effect.JobPoolVisible && effect.CanBindJob),
            JobGrey = jobGrey.Length,
            ItemGreyNoCall = itemGrey.Count(effect => effect.ImplementationStatus == "实现未解析"),
            ItemGreyUnwritable = itemGrey.Count(effect => effect.ImplementationStatus != "实现未解析"),
            JobGreyBeyondTable = jobGrey.Count(effect => effect.PersonalEffectId is { } id &&
                catalog.JobLayout is { Verified: true } layout && id >= layout.RowCount),
            JobGreyNoRow = jobGrey.Count(effect => effect.JobSlots.Count == 0)
        };

        Console.WriteLine($"ASSIGNMENT_POOL_AUDIT {name} effects={summary.Total} " +
            $"itemPool={summary.ItemPoolVisible} itemSelectable={summary.ItemSelectable} itemGrey={summary.ItemGrey} " +
            $"(noCall={summary.ItemGreyNoCall}/unwritable={summary.ItemGreyUnwritable}) " +
            $"jobPool={summary.JobPoolVisible} jobSelectable={summary.JobSelectable} jobGrey={summary.JobGrey} " +
            $"(beyondTable={summary.JobGreyBeyondTable}/noRow={summary.JobGreyNoRow}) jobLayout={summary.JobLayoutVerified}");
        foreach (var effect in itemGrey)
            Console.WriteLine($"  ITEM_GREY {effect.Key} {effect.Name} | {effect.ItemReadOnlyReason}");
        foreach (var effect in jobGrey)
            Console.WriteLine($"  JOB_GREY {effect.Key} {effect.Name} | {effect.JobReadOnlyReason}");

        if (string.IsNullOrWhiteSpace(outputDirectory)) return;
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "分配池审计-" + name + ".json");
        var items = catalog.Items.Select(item => new
        {
            item.Id, item.Name, item.Selector, item.Value, item.Category,
            item.SelectorFileOffset, item.ValueFileOffset, item.NeedsSpecialType, item.TypeValue,
            item.SharedItemIds
        }).ToArray();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Summary = summary, Items = items, Effects = rows }, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        Console.WriteLine($"ASSIGNMENT_POOL_AUDIT_JSON {path}");
    }
}
