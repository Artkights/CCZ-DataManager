using System.Globalization;
using DataManager.Core.Binary;
using DataManager.Core.Domain;

namespace DataManager.App;

public sealed partial class ExtendDataEditorPanel
{
    private void BuildForge(TableLayoutPanel body)
    {
        var metadata = SlotGrid([100, 0, 95, 0]);
        var nameHost = new ByteTextEditor { Dock = DockStyle.Fill, Height = 48, Margin = new Padding(4, 5, 14, 5), Name = "ExtendForgeNameEditor" };
        var name = nameHost.Editor;
        name.Name = "ExtendForgeName";
        void LoadName()
        {
            var document = _service.GetDocument(Category);
            var field = document.Definition.Fields.Single(field => field.Name == "名称");
            nameHost.Bind(TextFieldConstraints.Get(document.Definition, field, _service.GetRaw(Category, CurrentId, false)),
                Convert.ToString(_service.ReadValue(Category, CurrentId, "名称"), CultureInfo.InvariantCulture) ?? string.Empty);
        }
        LoadName();
        name.BackColor = Color.White;
        name.ForeColor = Color.Black;
        name.HideSelection = false;
        name.Select(name.TextLength, 0);
        Register("名称", name, () => name.Text, () => name.InputGuard.Prepare() ? name.Text : throw new InvalidDataException(name.InputGuard.Validation?.Error ?? "名称不能无损写回。"), LoadName);
        WireText(name);
        name.InputGuard.InputRejected += (_, _) => SetMessage(name.InputGuard.RejectionMessage ?? "名称输入被拒绝。", error: true);
        AddPair(metadata, 0, 0, "配方名称", nameHost);
        AddPair(metadata, 2, 0, "价格", NumberInput("价格", 0, uint.MaxValue, "ExtendForgePrice"));
        _materialCount = NumberInput("材料项数", 0, 8, "ExtendForgeMaterialCount");
        AddPair(metadata, 0, 1, "材料项数", _materialCount);
        AddPair(metadata, 2, 1, "商店编号", NumberInput("商店编号", 0, 255, "ExtendForgeShop"));
        var enabled = new CheckBox { Text = "启用此配方", AutoSize = true, Name = "ExtendForgeEnabled", Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 14, 5) };
        void LoadFlag()
        {
            var originalFlag = _service.ReadInt(Category, CurrentId, "标记");
            enabled.Checked = originalFlag != 0;
            _tips.SetToolTip(enabled, $"载入的启用原值为 {originalFlag}。只有主动切换时才写入 0 / 1。");
        }
        LoadFlag();
        Register("标记", enabled, () => enabled.Checked.ToString(), () => enabled.Checked ? 1 : 0,
            LoadFlag);
        enabled.CheckedChanged += (_, _) => NotifyPending();
        AddPair(metadata, 0, 2, "启用", enabled);
        AddPair(metadata, 2, 2, "剩余次数", NumberInput("剩余次数", 0, 255, "ExtendForgeRemaining"));
        AddBlock(body, metadata);
        AddBlock(body, new Label
        {
            AutoSize = true, MaximumSize = new Size(900, 0), Padding = new Padding(3, 5, 3, 7),
            Text = "前 N 槽是材料，其余是产物。装备材料填写等级，可指定精确等级；消耗品材料填写数量，产物数量使用完整字节。",
            Name = "ExtendForgeHelp"
        });
        var grid = SlotGrid([60, 0, 100, 120]);
        AddHeaders(grid, ["用途", "物品（输入名称或 ID 搜索）", "等级 / 数量", "等级条件"]);
        var choices = _service.GetItemOptions();
        for (var slot = 0; slot < 8; slot++)
        {
            var index = slot;
            var prefix = $"材料{slot + 1}";
            var id = IdInput(prefix + "ID", choices, ushort.MaxValue, $"ExtendForgeItem{slot}");
            var amount = new TextBox { Name = $"ExtendForgeAmount{slot}", Dock = DockStyle.Fill, Margin = new Padding(4, 5, 14, 5) };
            var exact = new CheckBox { Name = $"ExtendForgeExact{slot}", Text = "精确等级", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 5, 4, 5) };
            var role = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 7, 4, 7), Name = $"ExtendForgeRole{slot}" };
            var row = new ForgeRow(this, slot, id, amount, exact, role);
            _forgeRows.Add(row);
            row.Load();
            Register(prefix + "数量", amount, row.Signature, row.ReadAmount, row.Load);
            amount.TextChanged += (_, _) => NotifyPending();
            amount.Validating += (_, args) => { if (!_binding && HasPendingChanges) args.Cancel = !Flush(); };
            amount.KeyDown += (_, args) => { if (args.KeyCode == Keys.Enter) { Flush(); args.Handled = true; args.SuppressKeyPress = true; } };
            exact.CheckedChanged += (_, _) => NotifyPending();
            id.TextChanged += (_, _) => { if (!_binding) row.RefreshRole(); };
            grid.Controls.Add(role, 0, index + 1); grid.Controls.Add(id, 1, index + 1);
            grid.Controls.Add(amount, 2, index + 1); grid.Controls.Add(exact, 3, index + 1);
        }
        _materialCount.TextChanged += (_, _) => { if (!_binding) foreach (var row in _forgeRows) row.RefreshRole(); };
        AddBlock(body, grid);
    }

    private static void AddPair(TableLayoutPanel grid, int column, int row, string label, Control editor)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 7, 6, 7) }, column, row);
        grid.Controls.Add(editor, column + 1, row);
    }

    /// <summary>The amount's high bit is interpreted only in material rows; role changes preserve its raw byte.</summary>
    private sealed class ForgeRow(ExtendDataEditorPanel owner, int slot, SearchableIdComboBox id, TextBox amount, CheckBox exact, Label role)
    {
        private bool _material, _equipment, _consumable;
        private int _preservedHigh;
        private int _raw;
        private int _sourceItemId;
        private bool _changing;

        public void Load()
        {
            _raw = owner._service.ReadInt(ExtendDataCategory.Forge, owner.CurrentId, $"材料{slot + 1}数量");
            _sourceItemId = owner._service.ReadInt(ExtendDataCategory.Forge, owner.CurrentId, $"材料{slot + 1}ID");
            UpdateMode();
            Populate();
        }

        public void RefreshRole()
        {
            if (_changing) return;
            // Invalid edits remain on screen until corrected; changing a selector must not erase them.
            try { _raw = checked((int)ReadAmount()); }
            catch (InvalidDataException) { UpdateMode(); return; }
            var binding = owner._binding;
            owner._binding = true;
            try { UpdateMode(); Populate(); }
            finally { owner._binding = binding; }
        }

        private void UpdateMode()
        {
            var count = int.TryParse(owner._materialCount?.Text, out var value) ? value : owner._service.ReadInt(ExtendDataCategory.Forge, owner.CurrentId, "材料项数");
            _material = slot < count;
            var item = id.TryGetSelectedId(out var selected) ? selected : owner._service.ReadInt(ExtendDataCategory.Forge, owner.CurrentId, $"材料{slot + 1}ID");
            _equipment = item is >= 0 and <= 254;
            _consumable = item is >= 256 and <= 510;
            role.Text = $"{(_material ? "材料" : "产物")} {slot + 1}";
            exact.Enabled = _material && _equipment;
            exact.Visible = _material;
            owner._tips.SetToolTip(amount, _material && _equipment ? "装备材料等级为 0–127；勾选精确等级时设置最高位。" : _material && _consumable ? "消耗品材料数量为 0–127，禁止新增精确等级标记。" : "产物数量 / 未知 ID 的原值为 0–255。");
        }

        private void Populate()
        {
            _changing = true;
            try
            {
                _preservedHigh = _raw & 128;
                amount.Text = (_material && (_equipment || _consumable) ? _raw & 127 : _raw).ToString(CultureInfo.InvariantCulture);
                exact.Checked = (_raw & 128) != 0;
            }
            finally { _changing = false; }
        }

        public string Signature()
        {
            try { return Convert.ToString(ReadAmount(), CultureInfo.InvariantCulture) ?? string.Empty; }
            catch (InvalidDataException) { return $"invalid:{amount.Text}:{exact.Checked}"; }
        }

        public object ReadAmount()
        {
            var maximum = _material && (_equipment || _consumable) ? 127 : 255;
            var value = checked((int)ParseNumber(amount.Text, 0, maximum));
            if (_material && _equipment) return value | (exact.Checked ? 128 : 0);
            // Keep an existing anomalous high bit untouched until the user changes this quantity.
            if (_material && _consumable && id.TryGetSelectedId(out var currentItemId) && currentItemId == _sourceItemId && value == (_raw & 127)) return value | _preservedHigh;
            return value;
        }
    }
}
