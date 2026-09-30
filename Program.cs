using System;
using System.Drawing;
using System.Windows.Forms;

namespace UtilityTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainWindow());
        }
    }

    public sealed class MainWindow : Form
    {
        public MainWindow()
        {
            Text = "UtilityTool";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(960, 600);
            MinimumSize = new Size(640, 420);
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;
            MinimizeBox = true;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(245, 247, 250);

            var layout = new TableLayoutPanel
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
                ForeColor = Color.FromArgb(50, 95, 180),
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            }, 0, 0);
            layout.Controls.Add(new Label
            {
                Text = "Make Windows work your way.",
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 40, 55)
            }, 0, 1);
            layout.Controls.Add(new Label
            {
                Text = "Your utility workspace is ready.\n\nUse the title bar to move the window, or drag an edge to resize it.\nThe top-right buttons minimize, maximize or restore, and close the app.\n\nWindows usability tools can be added here next.",
                Dock = DockStyle.Fill,
                Padding = new Padding(0, 20, 0, 0),
                ForeColor = Color.FromArgb(75, 85, 100)
            }, 0, 2);
            layout.Controls.Add(new Label
            {
                Text = "Ready  •  UtilityTool starter",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.BottomLeft,
                ForeColor = Color.FromArgb(95, 105, 120)
            }, 0, 3);
            Controls.Add(layout);
        }
    }
}
