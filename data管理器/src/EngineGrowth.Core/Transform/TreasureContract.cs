namespace TransformManager.App;

/// <summary>Structural checks that decide whether a base may be written to.</summary>
public static class TreasureContract
{
    public static IReadOnlyList<string> Validate(GameBase game)
    {
        var errors = new List<string>();
        if (!game.CanEdit) return errors;

        if (game.CoverageTableVa == 0)
        {
            errors.Add("没有定位到覆盖表。");
            return errors;
        }
        int references = CountImmediateReferences(game, game.CoverageTableVa);
        if (references != 1)
            errors.Add($"覆盖表被 {references} 处代码引用，无法确认唯一消费者。");

        if (game.NamePoolHits < 10)
            errors.Add($"名称池只命中 {game.NamePoolHits}/18 个代表特效号，可能是未确认变体。");

        if (game.Cave is { } cave && !game.IsPatched)
        {
            var current = game.Exe.ReadBytes(cave.Address, Math.Min(cave.Length, 64));
            if (current.Length != Math.Min(cave.Length, 64) ||
                !current.AsSpan().SequenceEqual(cave.OriginalBytes.AsSpan(0, current.Length)))
                errors.Add("代码洞内容在扫描后发生变化，请重新扫描。");
        }
        return errors;
    }

    public static int CountImmediateReferences(GameBase game, uint value)
    {
        var pattern = BitConverter.GetBytes(value);
        int count = 0;
        foreach (var section in game.Exe.Sections)
        {
            if (!section.IsExecutable) continue;
            int start = checked((int)section.RawPointer);
            int end = start + checked((int)section.RawSize);
            for (int i = start; i <= end - pattern.Length; i++)
                if (game.Exe.Bytes.AsSpan(i, pattern.Length).SequenceEqual(pattern)) count++;
        }
        return count;
    }

    public static string Explain(GameBase game)
    {
        if (!game.CanEdit) return game.ReadOnlyReason;
        return "可以修改";
    }
}
