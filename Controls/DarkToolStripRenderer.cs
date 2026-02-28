using System.Drawing;
using System.Windows.Forms;

namespace MultiTerminal.Controls
{
    /// <summary>
    /// Custom ToolStrip renderer for dark mode with proper hover colors.
    /// </summary>
    public class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer() : base(new DarkColorTable())
        {
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Keep text white even when selected/hovered in dark mode
            if (e.Item.Selected || e.Item.Pressed)
            {
                e.TextColor = Color.White;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var button = e.Item as ToolStripButton;
            if (button != null && (button.Selected || button.Pressed))
            {
                var bounds = new Rectangle(Point.Empty, e.Item.Size);
                using (var brush = new SolidBrush(button.Pressed ? Color.FromArgb(70, 70, 74) : Color.FromArgb(62, 62, 66)))
                {
                    e.Graphics.FillRectangle(brush, bounds);
                }
            }
            else
            {
                base.OnRenderButtonBackground(e);
            }
        }

        protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
        {
            var button = e.Item as ToolStripDropDownButton;
            if (button != null && (button.Selected || button.Pressed))
            {
                var bounds = new Rectangle(Point.Empty, e.Item.Size);
                using (var brush = new SolidBrush(button.Pressed ? Color.FromArgb(70, 70, 74) : Color.FromArgb(62, 62, 66)))
                {
                    e.Graphics.FillRectangle(brush, bounds);
                }
            }
            else
            {
                base.OnRenderDropDownButtonBackground(e);
            }
        }
    }

    /// <summary>
    /// Custom color table for dark mode toolbar.
    /// </summary>
    public class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(45, 45, 48);
        public override Color MenuItemSelected => Color.FromArgb(62, 62, 66);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(62, 62, 66);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(62, 62, 66);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(70, 70, 74);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(70, 70, 74);
        public override Color MenuBorder => Color.FromArgb(51, 51, 55);
        public override Color MenuItemBorder => Color.FromArgb(62, 62, 66);
        public override Color ImageMarginGradientBegin => Color.FromArgb(45, 45, 48);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(45, 45, 48);
        public override Color ImageMarginGradientEnd => Color.FromArgb(45, 45, 48);
        public override Color SeparatorDark => Color.FromArgb(51, 51, 55);
        public override Color SeparatorLight => Color.FromArgb(51, 51, 55);
    }

    /// <summary>
    /// Standard light mode ToolStrip renderer.
    /// </summary>
    public class LightToolStripRenderer : ToolStripProfessionalRenderer
    {
        public LightToolStripRenderer() : base(new LightColorTable())
        {
        }
    }

    /// <summary>
    /// Custom color table for light mode toolbar.
    /// </summary>
    public class LightColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.FromArgb(246, 246, 246);
        public override Color MenuItemSelected => Color.FromArgb(209, 226, 242);
        public override Color MenuItemSelectedGradientBegin => Color.FromArgb(209, 226, 242);
        public override Color MenuItemSelectedGradientEnd => Color.FromArgb(209, 226, 242);
        public override Color MenuItemPressedGradientBegin => Color.FromArgb(188, 212, 235);
        public override Color MenuItemPressedGradientEnd => Color.FromArgb(188, 212, 235);
        public override Color MenuBorder => Color.FromArgb(204, 206, 219);
        public override Color MenuItemBorder => Color.FromArgb(152, 179, 206);
        public override Color ImageMarginGradientBegin => Color.FromArgb(246, 246, 246);
        public override Color ImageMarginGradientMiddle => Color.FromArgb(246, 246, 246);
        public override Color ImageMarginGradientEnd => Color.FromArgb(246, 246, 246);
        public override Color SeparatorDark => Color.FromArgb(204, 206, 219);
        public override Color SeparatorLight => Color.FromArgb(255, 255, 255);
    }
}
