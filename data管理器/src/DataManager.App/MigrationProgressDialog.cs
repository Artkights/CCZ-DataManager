using System.Diagnostics;
using System.Runtime.ExceptionServices;
using DataManager.Core.Migration;

namespace DataManager.App;

/// <summary>Keeps the owner stable while scanning and disk work runs off the UI thread.</summary>
internal sealed class MigrationProgressDialog : Form
{
    private readonly Label _stage = new() { Dock = DockStyle.Top, Height = 30, AutoEllipsis = true };
    private readonly Label _detail = new() { Dock = DockStyle.Top, Height = 64, AutoEllipsis = true };
    private readonly Label _elapsed = new() { Dock = DockStyle.Bottom, Height = 26 };
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Style = ProgressBarStyle.Marquee };
    private readonly Button _cancel = new() { Text = "取消", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly LatestProgress _progress = new();
    private readonly Stopwatch _watch = new();
    private bool _finished;
    private bool _commitStarted;

    private MigrationProgressDialog(string title)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(650, 210);
        Padding = new Padding(14);
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(_cancel);
        Controls.Add(_elapsed); Controls.Add(buttons); Controls.Add(_bar); Controls.Add(_detail); Controls.Add(_stage);
        _stage.Text = "正在准备…";
        _detail.Text = "正在后台处理，请稍候。预览可取消；写入目标后将完成提交或回滚。";
        _cancel.Click += (_, _) => RequestCancellation();
        FormClosing += (_, e) =>
        {
            if (_finished) return;
            e.Cancel = true;
            RequestCancellation();
        };
        _timer.Tick += (_, _) => RefreshProgress();
    }

    internal static T Run<T>(IWin32Window owner, string title, Func<IProgress<MigrationProgress>, CancellationToken, T> operation)
    {
        using var dialog = new MigrationProgressDialog(title);
        T? result = default;
        Exception? failure = null;
        dialog.Shown += async (_, _) =>
        {
            dialog._watch.Start();
            dialog._timer.Start();
            try { result = await Task.Run(() => operation(dialog._progress, dialog._cancellation.Token)); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                dialog._finished = true;
                dialog._timer.Stop();
                dialog.Close();
            }
        };
        dialog.ShowDialog(owner);
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    private void RequestCancellation()
    {
        if (_commitStarted || _cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        _cancel.Enabled = false;
        _cancel.Text = "正在取消…";
        _detail.Text = "正在等待当前安全步骤结束；若已开始提交，将完成提交或回滚。";
    }

    private void RefreshProgress()
    {
        var progress = _progress.Current;
        if (progress != null)
        {
            _stage.Text = progress.Stage;
            if (!_cancellation.IsCancellationRequested) _detail.Text = progress.Detail;
            if (progress.Stage.Contains("提交目标", StringComparison.Ordinal) || progress.Stage.Contains("回滚", StringComparison.Ordinal))
            {
                _commitStarted = true;
                _cancel.Enabled = false;
                _cancel.Text = "提交中";
            }
            if (progress.Total > 0)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = progress.Total;
                _bar.Value = Math.Clamp(progress.Completed, 0, progress.Total);
            }
            else _bar.Style = ProgressBarStyle.Marquee;
        }
        _elapsed.Text = $"已用时 {_watch.Elapsed:hh\\:mm\\:ss}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _cancellation.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class LatestProgress : IProgress<MigrationProgress>
    {
        private MigrationProgress? _current;
        public MigrationProgress? Current => Volatile.Read(ref _current);
        public void Report(MigrationProgress value) => Volatile.Write(ref _current, value);
    }
}
