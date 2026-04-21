using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MultiTerminal
{
    /// <summary>
    /// Animated splash screen with a progress border that fills from red to green.
    /// Green progress starts at top-center and traces clockwise around the border.
    /// </summary>
    public class SplashScreen : Form
    {
        private float _displayProgress = 0f;      // Current progress (0.0 to 1.0)
        private bool _loadingComplete = false;    // Are all terminals loaded?
        private readonly Stopwatch _stopwatch = new Stopwatch();
        private const float ANIMATION_DURATION_MS = 2000f;  // 2 seconds total

        private readonly int _borderWidth = 8;
        private readonly Timer _animationTimer;

        /// <summary>
        /// Event fired when animation is complete AND loading is done.
        /// </summary>
        public event EventHandler AnimationComplete;

        public SplashScreen()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(30, 30, 30);
            Size = new Size(350, 180);
            ShowInTaskbar = false;
            DoubleBuffered = true;

            // Timer for smooth animation (starts when form is shown)
            _animationTimer = new Timer { Interval = 16 };  // ~60fps
            _animationTimer.Tick += OnAnimationTick;
            _animationTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Start animation when form is actually visible
            _stopwatch.Start();
        }

        /// <summary>
        /// Signal that all terminals have finished loading.
        /// </summary>
        public void SetLoadingComplete()
        {
            _loadingComplete = true;
        }

        private void OnAnimationTick(object sender, EventArgs e)
        {
            if (!_stopwatch.IsRunning) return;

            // Calculate progress based on elapsed time (Stopwatch is precise)
            float elapsed = _stopwatch.ElapsedMilliseconds;
            _displayProgress = Math.Min(1f, elapsed / ANIMATION_DURATION_MS);
            Invalidate();

            // Fire completion when both animation done AND loading complete
            if (_displayProgress >= 1f && _loadingComplete)
            {
                _animationTimer.Stop();
                AnimationComplete?.Invoke(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int b = _borderWidth;

            // Draw red border (background)
            using (var redPen = new Pen(Color.FromArgb(180, 60, 60), b))
            {
                g.DrawRectangle(redPen, b / 2, b / 2, w - b, h - b);
            }

            // Draw green progress over red
            // Progress traces clockwise from top-center:
            // top-right, right, bottom, left, top-left back to center
            float perimeterTotal = 2 * (w - b) + 2 * (h - b);
            float greenLength = _displayProgress * perimeterTotal;

            if (greenLength > 0)
            {
                using (var greenPen = new Pen(Color.FromArgb(60, 180, 60), b))
                {
                    greenPen.StartCap = LineCap.Flat;
                    greenPen.EndCap = LineCap.Round;

                    float drawn = 0;
                    float topHalf = (w - b) / 2f;

                    // Top edge right half (center to right)
                    float topRight = Math.Min(greenLength, topHalf);
                    if (topRight > 0)
                        g.DrawLine(greenPen, w / 2, b / 2, w / 2 + topRight, b / 2);
                    drawn += topRight;

                    // Right edge (top to bottom)
                    if (drawn < greenLength)
                    {
                        float rightLen = Math.Min(greenLength - drawn, h - b);
                        g.DrawLine(greenPen, w - b / 2, b / 2, w - b / 2, b / 2 + rightLen);
                        drawn += rightLen;
                    }

                    // Bottom edge (right to left)
                    if (drawn < greenLength)
                    {
                        float bottomLen = Math.Min(greenLength - drawn, w - b);
                        g.DrawLine(greenPen, w - b / 2, h - b / 2, w - b / 2 - bottomLen, h - b / 2);
                        drawn += bottomLen;
                    }

                    // Left edge (bottom to top)
                    if (drawn < greenLength)
                    {
                        float leftLen = Math.Min(greenLength - drawn, h - b);
                        g.DrawLine(greenPen, b / 2, h - b / 2, b / 2, h - b / 2 - leftLen);
                        drawn += leftLen;
                    }

                    // Top edge left half (left to center)
                    if (drawn < greenLength)
                    {
                        float topLeftLen = Math.Min(greenLength - drawn, topHalf);
                        g.DrawLine(greenPen, b / 2, b / 2, b / 2 + topLeftLen, b / 2);
                    }
                }
            }

            // Draw title centered
            using (var font = new Font("Segoe UI", 24, FontStyle.Bold))
            using (var brush = new SolidBrush(Color.White))
            {
                var text = "MultiTerminal";
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, brush, (w - size.Width) / 2, (h - size.Height) / 2 - 10);
            }

            // Draw loading text
            using (var font = new Font("Segoe UI", 10))
            using (var brush = new SolidBrush(Color.Gray))
            {
                var text = "Loading...";
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, brush, (w - size.Width) / 2, h - 40);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _animationTimer?.Stop();
            _animationTimer?.Dispose();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _animationTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
