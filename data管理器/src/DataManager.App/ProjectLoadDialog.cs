using DataManager.Core.Editing;
using System.Runtime.ExceptionServices;

namespace DataManager.App;

/// <summary>Only a complete, uncancelled workspace may replace the live editor session.</summary>
internal sealed class ProjectLoadDialog : Form
{
    private readonly CancellationTokenSource _cancellation = new();
    private bool _finished;

    private ProjectLoadDialog()
    {
        Text = "打开项目";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(500, 130);
        Padding = new Padding(14);
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var status = new Label { Dock = DockStyle.Top, Height = 34, Text = "正在读取项目，请稍候…" };
        var progress = new ProgressBar { Dock = DockStyle.Top, Height = 20, Style = ProgressBarStyle.Marquee };
        var cancel = new Button { Text = "取消", AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        Controls.Add(progress); Controls.Add(status); Controls.Add(buttons);
        void Cancel()
        {
            if (_finished || _cancellation.IsCancellationRequested) return;
            _cancellation.Cancel(); cancel.Enabled = false; status.Text = "正在取消读取…";
        }
        cancel.Click += (_, _) => Cancel();
        FormClosing += (_, args) => { if (!_finished) { args.Cancel = true; Cancel(); } };
    }

    internal static ProjectWorkspace? LoadWorkspace(IWin32Window owner, string root, string? profileId = null, string? variantId = null)
    {
        using var dialog = new ProjectLoadDialog();
        ProjectWorkspace? result = null;
        Exception? failure = null;
        dialog.Shown += async (_, _) =>
        {
            try
            {
                result = await Task.Run(() =>
                {
                    var candidate = new ProjectWorkspace();
                    candidate.Open(root, profileId, variantId, dialog._cancellation.Token);
                    return candidate;
                }, dialog._cancellation.Token);
                if (dialog._cancellation.IsCancellationRequested) result = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { failure = ex; }
            finally { dialog._finished = true; dialog.Close(); }
        };
        dialog.ShowDialog(owner);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellation.Dispose();
        base.Dispose(disposing);
    }
}
