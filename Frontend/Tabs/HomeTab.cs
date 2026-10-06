using System;
using System.Drawing;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Tabs
{
    internal sealed class HomeTab : BufferedTabPage
    {
        internal HomeTab() : base("Home")
        {
            BackColor = AppTheme.Surface;
            var layout = new FrostedLayout
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(36),
                ColumnCount = 1,
                RowCount = 4
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

            layout.Controls.Add(new Label
            {
                Text = "WINDOWS UTILITIES",
                Dock = DockStyle.Fill,
                ForeColor = AppTheme.Accent,
                Font = new Font("Segoe UI", 9, FontStyle.Regular)
            }, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "Make Windows work your way.",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24, FontStyle.Bold),
                ForeColor = AppTheme.Ink
            }, 0, 1);
            layout.Controls.Add(new Label
            {
                Text = "A calmer space for everyday Windows tasks.\n\nSHUTDOWN\nChoose a delay, schedule a shutdown, or cancel a pending one.\n\nLOCK\nChoose apps to protect with your custom password.\n\nSOUND\nPlay Windows audio through multiple speakers or headphones.\n\nMove, resize, and minimize this window using the standard Windows controls.",
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 20, 0, 0),
                ForeColor = AppTheme.Muted
            }, 0, 2);
            layout.Controls.Add(new Label
            {
                Text = "Ready  •  Your Windows utility workspace",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = AppTheme.Muted
            }, 0, 3);

            Controls.Add(layout);
            AppTheme.Apply(this);
        }
    }
}
