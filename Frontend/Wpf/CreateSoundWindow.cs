using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using UtilityTool.Backend;
using global::Wpf.Ui.Controls;

namespace UtilityTool.Frontend.Wpf
{
    internal sealed class CreateSoundWindow : FluentWindow
    {
        private readonly System.Windows.Controls.TextBox name = new System.Windows.Controls.TextBox { MaxLength = 64, Height = 42 };
        private readonly System.Windows.Controls.TextBox mp3 = new System.Windows.Controls.TextBox { IsReadOnly = true, Height = 42 };
        private readonly System.Windows.Controls.TextBox image = new System.Windows.Controls.TextBox { IsReadOnly = true, Height = 42 };
        private readonly System.Windows.Controls.TextBlock status = Ui.Text("", 13, true);
        private readonly System.Windows.Controls.CheckBox reduceSize = new System.Windows.Controls.CheckBox { Content = "Reduce file size", Margin = new Thickness(0, 16, 0, 0) };
        private readonly global::Wpf.Ui.Controls.Button create = Ui.Button("Create sound", true), cancel = Ui.Button("Cancel");
        private readonly SoundboardStore store;
        private readonly SoundboardSound editing;
        private readonly System.Collections.Generic.List<global::Wpf.Ui.Controls.Button> browseButtons = new System.Collections.Generic.List<global::Wpf.Ui.Controls.Button>();
        private bool busy;
        internal SoundboardSound CreatedSound { get; private set; }
        internal CreateSoundWindow(Window owner, SoundboardStore store, SoundboardSound editing = null)
        {
            this.store = store; this.editing = editing; Owner = owner; Title = editing == null ? "Create new sound" : "Edit sound";
            if (editing != null) { name.Text = editing.Name; mp3.Text = editing.AudioPath; image.Text = editing.ImagePath ?? ""; create.Content = "Save changes"; }
            Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ExtendsContentIntoTitleBar = true;
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 39));
            var layout = new Grid { Background = Background };
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TitleBar { Title = "UtilityTool", ShowMinimize = false, ShowMaximize = false, ForceShutdown = false });
            var body = new StackPanel { Margin = new Thickness(28) };
            body.Children.Add(Ui.Heading(Title, editing == null ? "Give your sound a name, then choose its MP3 and an optional image." : "Change the name, or browse for a replacement MP3 or image."));
            AddLabel(body, "Sound name"); body.Children.Add(name);
            AddLabel(body, "MP3 file"); body.Children.Add(FilePicker(mp3, false));
            AddLabel(body, "Image (optional)"); body.Children.Add(FilePicker(image, true));
            var hint = Ui.Text(editing == null ? "A copy is saved in your sound library. Your original files stay where they are." : "Your current files stay unchanged unless you choose replacements. Your original files are preserved.", 13, true); hint.Margin = new Thickness(0, 16, 0, 0); body.Children.Add(hint);
            body.Children.Add(reduceSize);
            var sizeHint = Ui.Text("Fits images within 512 × 512 pixels and uses 128 kbps MP3 when it saves space. MP3 compression can reduce audio quality. Applies to new or replaced files.", 12, true); sizeHint.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(sizeHint);
            status.MinHeight = 42; status.Margin = new Thickness(0, 10, 0, 0); body.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(create); actions.Children.Add(cancel); body.Children.Add(actions);
            Grid.SetRow(body, 1); layout.Children.Add(body); Content = layout;
            System.Windows.Automation.AutomationProperties.SetName(name, "Sound name"); System.Windows.Automation.AutomationProperties.SetName(mp3, "MP3 file"); System.Windows.Automation.AutomationProperties.SetName(image, "Sound image");
            System.Windows.Automation.AutomationProperties.SetName(reduceSize, "Reduce sound file size");
            create.IsDefault = true; cancel.IsCancel = true;
            create.Click += async delegate { await Submit(); }; cancel.Click += delegate { if (!busy) DialogResult = false; };
            ContentRendered += delegate { name.Focus(); if (editing != null) name.SelectAll(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (busy) e.Cancel = true; };
        }
        private static void AddLabel(StackPanel body, string text)
        { var label = Ui.Text(text, 13); label.Margin = new Thickness(0, body.Children.Count > 2 ? 16 : 0, 0, 6); body.Children.Add(label); }
        private UIElement FilePicker(System.Windows.Controls.TextBox field, bool images)
        {
            var row = new DockPanel(); var browse = Ui.Button("Browse…"); browse.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(field);
            browseButtons.Add(browse);
            browse.Click += delegate
            {
                if (busy) return;
                var dialog = new OpenFileDialog { Title = images ? "Choose a sound image" : "Choose an MP3", Filter = images ? "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico" : "MP3 audio|*.mp3", CheckFileExists = true, Multiselect = false };
                if (dialog.ShowDialog(this) == true) field.Text = dialog.FileName;
            };
            return row;
        }
        private async Task Submit()
        {
            if (busy) return;
            string soundName = name.Text, audioPath = mp3.Text, imagePath = image.Text;
            if (editing != null)
            {
                if (string.Equals(audioPath, editing.AudioPath, StringComparison.OrdinalIgnoreCase)) audioPath = null;
                if (string.Equals(imagePath, editing.ImagePath ?? "", StringComparison.OrdinalIgnoreCase)) imagePath = null;
            }
            bool reduceFileSize = reduceSize.IsChecked == true;
            busy = true; SetControlsEnabled(false); Ui.Status(status, reduceFileSize ? "Reducing selected files and saving…" : editing == null ? "Adding sound…" : "Saving changes…");
            try
            {
                CreatedSound = await Task.Run(() => editing == null ? store.Import(soundName, audioPath, imagePath, reduceFileSize) : store.Update(editing.Id, soundName, audioPath, imagePath, reduceFileSize));
                busy = false; DialogResult = true;
            }
            catch (Exception error) { Ui.Status(status, error.Message, true); }
            finally { busy = false; SetControlsEnabled(true); }
        }
        private void SetControlsEnabled(bool value)
        { create.IsEnabled = cancel.IsEnabled = name.IsEnabled = mp3.IsEnabled = image.IsEnabled = reduceSize.IsEnabled = value; foreach (var browse in browseButtons) browse.IsEnabled = value; }
    }
}
