using DataManager.Images;
using ImageManager.Core;

namespace DataManager.App;

public sealed record SImageLocationCandidate(string Label, int PhysicalImageNumber);

public static class SImageLocationCandidates
{
    public static IReadOnlyList<SImageLocationCandidate> Create(int sId, int jobId)
    {
        var mapping = new SPhysicalMappingService();
        if (sId == 0)
        {
            var first = mapping.Resolve(sId, jobId, 1).ImageNumber;
            return new[] { "我军", "友军", "敌军" }.Select((label, index) => new SImageLocationCandidate(
                $"兵种 {jobId} · {label}", first + index)).ToArray();
        }
        return Enumerable.Range(1, sId is >= 1 and <= 32 ? 3 : 1)
            .Select(turn => new SImageLocationCandidate(sId <= 32 ? $"第 {turn} 转" : "单转形象",
                mapping.Resolve(sId, jobId, turn).ImageNumber)).ToArray();
    }
}

internal sealed class SImageLocationDialog : Form
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<ResourcePreviewAnimation> _owned = [];
    public int SelectedPhysicalImageNumber { get; private set; }

    public SImageLocationDialog(ResourceCatalogAdapter resources, string root, int personId, int sId,
        IReadOnlyList<SImageLocationCandidate> candidates)
    {
        Text = "定位人物 S 形象";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(730, 360); MinimumSize = new Size(560, 330);
        var explanation = new Label { Dock = DockStyle.Top, Height = 64, Padding = new Padding(12),
            Text = $"人物 {personId} · 当前草稿紧凑 S={sId}。请选择实际形象：\n每个候选独立包含移动、攻击、特技三个文件；导入只覆盖选中的物理图号。" };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = candidates.Count, Padding = new Padding(8) };
        var previews = new List<(SImageLocationCandidate Candidate, PictureBox Picture, Button Select)>();
        foreach (var candidate in candidates)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / candidates.Count));
            var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(6), BorderStyle = BorderStyle.FixedSingle };
            var label = new Label { Dock = DockStyle.Top, Height = 45, TextAlign = ContentAlignment.MiddleCenter,
                Text = $"{candidate.Label}\nUnit #{candidate.PhysicalImageNumber}" };
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(238, 240, 242) };
            var select = new Button { Dock = DockStyle.Bottom, Height = 34, Text = "正在检查…", Enabled = false };
            select.Click += (_, _) => { SelectedPhysicalImageNumber = candidate.PhysicalImageNumber; DialogResult = DialogResult.OK; Close(); };
            card.Controls.Add(picture); card.Controls.Add(label); card.Controls.Add(select); grid.Controls.Add(card);
            previews.Add((candidate, picture, select));
        }
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8) }; footer.Controls.Add(cancel);
        CancelButton = cancel;
        Controls.Add(grid); Controls.Add(explanation); Controls.Add(footer);
        Shown += async (_, _) =>
        {
            foreach (var (candidate, picture, select) in previews)
            {
                try
                {
                    var animation = await resources.DecodePhysicalSFirstFrameAsync(root, candidate.PhysicalImageNumber, _cancellation.Token);
                    if (IsDisposed || _cancellation.IsCancellationRequested) { animation.Dispose(); return; }
                    _owned.Add(animation);
                    picture.Image = animation.Frames.FirstOrDefault();
                    select.Enabled = picture.Image != null;
                    select.Text = select.Enabled ? "定位此形象" : "资源不完整";
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or InvalidDataException)
                {
                    if (IsDisposed) return;
                    select.Text = "资源不可用";
                    select.AccessibleDescription = ex.Message;
                }
            }
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation.Cancel();
            foreach (var animation in _owned) animation.Dispose();
            _owned.Clear();
        }
        base.Dispose(disposing);
    }
}
