using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MultiTerminal.Services;

namespace MultiTerminal.Dialogs
{
    /// <summary>
    /// Shows a Move Project while it runs (ticket 6f879683): the current step, a progress bar while files
    /// copy, and Cancel. The move itself runs on a worker thread; this form only reports. Cancel is honoured
    /// only during the copy, the one long step that can be undone cleanly.
    /// </summary>
    internal sealed class MoveProjectProgressForm : Form
    {
        private readonly Label _step;
        private readonly ProgressBar _bar;
        private readonly Button _cancel;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Func<IProgress<RelocationProgress>, CancellationToken, RelocationResult> _work;

        private MoveProjectProgressForm(string title, Func<IProgress<RelocationProgress>, CancellationToken, RelocationResult> work)
        {
            _work = work;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            ControlBox = false;
            ClientSize = new Size(460, 120);
            Font = new Font("Segoe UI", 9.5f);

            _step = new Label { Left = 16, Top = 16, Width = 428, Height = 22, Text = "Starting…", AutoEllipsis = true };
            _bar = new ProgressBar { Left = 16, Top = 44, Width = 428, Height = 18, Style = ProgressBarStyle.Marquee };
            _cancel = new Button { Text = "Cancel", Width = 90, Height = 28, Left = 354, Top = 76, Enabled = false };
            _cancel.Click += (s, e) =>
            {
                _cancel.Enabled = false;
                _step.Text = "Cancelling…";
                _cts.Cancel();
            };
            Controls.AddRange(new Control[] { _step, _bar, _cancel });
        }

        /// <summary>The result of the move, set when the form closes.</summary>
        public RelocationResult Result { get; private set; }

        /// <summary>Runs <paramref name="work"/> on a worker thread behind a modal progress window.</summary>
        public static RelocationResult Run(IWin32Window owner, string title, Func<IProgress<RelocationProgress>, CancellationToken, RelocationResult> work)
        {
            using var form = new MoveProjectProgressForm(title, work);
            form.ShowDialog(owner);
            return form.Result;
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            var progress = new Progress<RelocationProgress>(p =>
            {
                _step.Text = p.Step;
                if (p.Percent is int percent)
                {
                    _bar.Style = ProgressBarStyle.Continuous;
                    _bar.Value = Math.Clamp(percent, 0, 100);
                    _cancel.Enabled = !_cts.IsCancellationRequested; // the copy is the step Cancel can undo
                }
                else
                {
                    _bar.Style = ProgressBarStyle.Marquee;
                    _cancel.Enabled = false; // only the copy can be cancelled
                }
            });

            try
            {
                Result = await Task.Run(() => _work(progress, _cts.Token)).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // Any failure must close the window and reach the summary, not crash MT.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Result = new RelocationResult();
                Result.Refusals.Add("The move stopped unexpectedly: " + ex.Message);
            }

            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts.Dispose();
                _step.Dispose();
                _bar.Dispose();
                _cancel.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
