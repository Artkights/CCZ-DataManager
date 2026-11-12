namespace RangeManager.App;

/// <summary>
/// Describes whether the center of an existing range binding may be changed.
/// The decision is kept outside the editor so the UI and validation code can
/// present the same reason for native special cases and unknown contracts.
/// </summary>
public readonly record struct RangeCenterCapability(bool CanChange, string Reason)
{
    public static RangeCenterCapability Evaluate(GameBase game, RangeBinding binding)
    {
        if (binding.Key.Kind != RangeKind.Pierce)
            return new(false, "攻击范围固定以自身为中心");

        if (!binding.Editable)
            return new(false, string.IsNullOrWhiteSpace(binding.ReadOnlyReason)
                ? "该穿透编号不可编辑" : binding.ReadOnlyReason);

        if (binding.Key.Id == 0)
            return new(false, "原生编号 0 表示不追加穿透格，不能切换中心");

        // These two 6.5 entries use native control flow that is not represented
        // by the ordinary coordinate/weight buffer. A managed replacement may
        // opt into a normal definition, while an untouched native binding must
        // retain its original center semantics.
        if (game.Version == EngineVersion.V65 && binding.Key.Id == 0x0A && !binding.IsManaged)
            return new(false, "6.5 穿透 0A 使用独立原生谓词，不能切换中心");
        if (game.Version == EngineVersion.V65 && binding.Key.Id == 0x0B && !binding.IsManaged)
            return new(false, "6.5 穿透 0B 是原生双侧加直线组合，中心语义不能单独切换");

        // Native bindings must carry the contract identity used when they were
        // read. Managed records have already passed the write-contract check
        // in RangeEditSession and are allowed to carry their own center.
        if (!binding.IsManaged && !string.Equals(binding.ContractId,
                NativeRangeContract.Id(game, RangeKind.Pierce), StringComparison.Ordinal))
            return new(false, "当前穿透范围契约未匹配，不能切换中心");

        return new(true, "");
    }
}
