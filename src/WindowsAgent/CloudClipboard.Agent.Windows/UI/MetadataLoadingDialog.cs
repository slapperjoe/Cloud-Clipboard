using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudClipboard.Agent.Windows.UI;

public sealed class MetadataLoadingDialog : Form
{
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;

    public MetadataLoadingDialog()
    {
        Text = "Preparing Provisioning Dialog";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        ClientSize = new Size(420, 160);
        MinimumSize = new Size(380, 150);
        MaximizeBox = false;
        MinimizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _statusLabel = new Label
        {
            Text = "Loading Azure metadata...",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12)
        };

        _progressBar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Dock = DockStyle.Fill,
            MarqueeAnimationSpeed = 30,
            Height = 20
        };

        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_progressBar, 0, 1);

        Controls.Add(layout);
    }

    public void UpdateStatus(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateStatus(message)));
            return;
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            _statusLabel.Text = message;
        }
    }

    public void Complete(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => Complete(message)));
            return;
        }

        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.MarqueeAnimationSpeed = 0;
        _progressBar.Value = _progressBar.Maximum;
        if (!string.IsNullOrWhiteSpace(message))
        {
            _statusLabel.Text = message;
        }
    }

    private void CloseWithResult(DialogResult result)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => CloseWithResult(result)));
            return;
        }

        DialogResult = result;
        Close();
    }

    public static Task<TResult> RunAsync<TResult>(Func<MetadataLoadingDialog, CancellationToken, Task<TResult>> work, Icon icon, CancellationToken cancellationToken)
    {
        if (work is null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var dialog = new MetadataLoadingDialog
                {
                    Icon = icon,
                    ShowIcon = true
                };

                using var registration = cancellationToken.Register(() =>
                {
                    if (dialog.IsHandleCreated)
                    {
                        dialog.BeginInvoke(new Action(() => dialog.CloseWithResult(DialogResult.Cancel)));
                    }
                });

                dialog.Load += async (_, _) =>
                {
                    try
                    {
                        var result = await work(dialog, cancellationToken).ConfigureAwait(true);
                        tcs.TrySetResult(result);
                        dialog.CloseWithResult(DialogResult.OK);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(cancellationToken);
                        dialog.CloseWithResult(DialogResult.Cancel);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                        dialog.CloseWithResult(DialogResult.Abort);
                    }
                };

                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "CloudClipboard.MetadataLoadingDialog"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }
}
