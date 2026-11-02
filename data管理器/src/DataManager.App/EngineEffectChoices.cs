namespace DataManager.App;

/// <summary>One selectable effect number with the same name shown by the item/job effect pages.</summary>
public sealed record EngineEffectChoiceItem(int Id, string Name, bool Fallback = false)
{
    public override string ToString() => $"{Id}：{Name}";
}

internal static class EngineEffectChoiceCombo
{
    public static void Fill(ComboBox combo, IReadOnlyList<EngineEffectChoiceItem> choices)
    {
        combo.BeginUpdate();
        try
        {
            combo.Items.Clear();
            foreach (var choice in choices) combo.Items.Add(choice);
        }
        finally { combo.EndUpdate(); }
    }

    public static int? Selected(ComboBox combo)
        => combo.SelectedItem is EngineEffectChoiceItem choice ? choice.Id : null;

    public static void Select(ComboBox combo, int? value)
    {
        for (var index = combo.Items.Count - 1; index >= 0; index--)
            if (combo.Items[index] is EngineEffectChoiceItem { Fallback: true }) combo.Items.RemoveAt(index);
        if (value is not { } id) { combo.SelectedIndex = -1; return; }
        for (var index = 0; index < combo.Items.Count; index++)
            if (combo.Items[index] is EngineEffectChoiceItem choice && choice.Id == id)
            {
                combo.SelectedIndex = index;
                return;
            }
        combo.Items.Add(new EngineEffectChoiceItem(id, "未分配", true));
        combo.SelectedIndex = combo.Items.Count - 1;
    }
}
