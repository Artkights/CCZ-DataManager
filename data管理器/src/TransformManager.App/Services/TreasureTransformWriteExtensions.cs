using EngineGrowth.Core;
namespace TransformManager.App;
public static class TreasureTransformWriteExtensions
{
    public static SaveOutcome Apply(this TreasureTransformService service, IReadOnlyList<SlotEdit> edits)
    {
        try
        {
            var plan = GrowthPatchCoordinator.Prepare(service.Base.RootPath, service.Base.Exe.Bytes,
                service.Catalog, edits, null);
            var result = TransformSaveTransaction.Commit(service.Base.ExePath, plan.BeforeBytes, plan.AfterBytes, plan.Summary);
            return result;
        }
        catch (Exception ex) { return new SaveOutcome(false, ex.Message, null); }
    }
}
