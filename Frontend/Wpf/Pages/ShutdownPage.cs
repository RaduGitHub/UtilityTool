using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class ShutdownPage : Grid, IDisposable
    {
        internal readonly DurationInput Duration = new DurationInput();
        private readonly global::Wpf.Ui.Controls.Button start = Ui.Button("Schedule shutdown", true), cancel = Ui.Button("Cancel shutdown");
        private readonly global::Wpf.Ui.Controls.Button addPreset = Ui.Button("Add time", true), editPreset = Ui.Button("Modify selected"), deletePreset = Ui.Button("Delete selected");
        private readonly TextBlock status = Ui.Text("Choose a duration to get started.", 14, true), presetStatus = Ui.Text("Double-click a time, or select it and press Enter, to load the timer.", 13, true), emptyPresets = Ui.Text("No saved times yet. Add a time to get started.", 14, true);
        private readonly ListBox presets = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), Foreground = Brushes.WhiteSmoke, MinHeight = 130 };
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly ShutdownPresetStore presetStore;
        private readonly Action<string> execute;
        private readonly Func<Window, ShutdownPreset, ShutdownPreset> editor;
        private readonly bool preview;
        private List<ShutdownPreset> presetValues = new List<ShutdownPreset>();
        private DateTimeOffset? scheduled;
        private bool busy, disposed, presetsReady, bindingPresets;
        internal ShutdownPage(bool preview, ShutdownPresetStore presetStore = null, Action<string> execute = null, Func<Window, ShutdownPreset, ShutdownPreset> editPreset = null)
        {
            this.preview = preview; this.presetStore = presetStore ?? new ShutdownPresetStore(); this.execute = execute ?? ShutdownService.Execute; editor = editPreset ?? OpenEditor;
            Margin = new Thickness(32, 28, 32, 24); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition());
            Children.Add(Ui.Heading("Shutdown timer", "Save useful times and choose when Windows should switch off."));
            var body = new Grid { Margin = new Thickness(0, 12, 0, 12), MaxHeight = 520, VerticalAlignment = VerticalAlignment.Stretch }; body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); Grid.SetRow(body, 1); Children.Add(body);
            var saved = BuildPresets(); body.Children.Add(saved); var timerPanel = BuildTimer(); Grid.SetColumn(timerPanel, 2); body.Children.Add(timerPanel);
            presets.SelectionChanged += delegate { if (!bindingPresets) UpdateControls(); };
            presets.MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) { if (FindRow(e.OriginalSource as DependencyObject) != null) { ApplySelected(); e.Handled = true; } };
            presets.PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplySelected(); e.Handled = true; } };
            start.Click += async delegate { await Schedule(); }; cancel.Click += async delegate { await Cancel(); }; timer.Tick += delegate { UpdateCountdown(); };
            addPreset.Click += async delegate { await EditSavedTime(null); }; this.editPreset.Click += async delegate { await EditSavedTime(presets.SelectedItem as ShutdownPreset); }; deletePreset.Click += async delegate { await DeleteSelectedTime(); };
            LoadPresets(); UpdateControls();
        }
        private FrameworkElement BuildTimer()
        {
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var icon = Ui.Text("\uE823", 28); icon.FontFamily = new FontFamily("Segoe MDL2 Assets"); icon.Foreground = Ui.Accent; icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 16); center.Children.Add(icon); Duration.HorizontalAlignment = HorizontalAlignment.Center; center.Children.Add(Duration);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) }; actions.Children.Add(start); actions.Children.Add(cancel); center.Children.Add(actions);
            status.TextAlignment = TextAlignment.Center; status.Margin = new Thickness(0, 16, 0, 0); status.MinHeight = 22; status.MaxHeight = 48; status.MaxWidth = 480; center.Children.Add(status);
            var note = Ui.Text("Save your work before the timer ends. A scheduled shutdown continues after UtilityTool closes.", 13, true); note.TextAlignment = TextAlignment.Center; note.MaxWidth = 480; note.Margin = new Thickness(0, 16, 0, 0); center.Children.Add(note);
            var card = Ui.Card(center, new Thickness(24)); System.Windows.Automation.AutomationProperties.SetName(card, "Shutdown timer panel"); return card;
        }
        private FrameworkElement BuildPresets()
        {
            var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition()); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = Ui.Text("Saved times", 20); title.FontWeight = FontWeights.SemiBold; title.Margin = new Thickness(0, 0, 0, 16); content.Children.Add(title);
            var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 10, 12, 10))); style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 8))); style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(46, 53, 64)))); style.Setters.Add(new Setter(Control.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(65, 75, 89)))); style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1))); style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.WhiteSmoke)); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent }); border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter))); style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(55, 67, 80)))); style.Triggers.Add(hover);
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromRgb(28, 67, 92)))); selected.Setters.Add(new Setter(Control.BorderBrushProperty, Ui.Accent)); style.Triggers.Add(selected); presets.ItemContainerStyle = style;
            var row = new FrameworkElementFactory(typeof(StackPanel)); var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Name")); name.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Name")); name.SetValue(TextBlock.FontSizeProperty, 15.0); name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); row.AppendChild(name);
            var length = new FrameworkElementFactory(typeof(TextBlock)); length.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("DurationLabel")); length.SetValue(TextBlock.FontSizeProperty, 13.0); length.SetValue(TextBlock.ForegroundProperty, Ui.Muted); length.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0)); row.AppendChild(length); presets.ItemTemplate = new DataTemplate { VisualTree = row };
            ScrollViewer.SetHorizontalScrollBarVisibility(presets, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(presets, ScrollBarVisibility.Auto); System.Windows.Automation.AutomationProperties.SetName(presets, "Saved shutdown times");
            emptyPresets.HorizontalAlignment = HorizontalAlignment.Center; emptyPresets.VerticalAlignment = VerticalAlignment.Center; emptyPresets.TextAlignment = TextAlignment.Center; emptyPresets.Margin = new Thickness(12);
            var list = new Grid(); list.Children.Add(presets); list.Children.Add(emptyPresets); Grid.SetRow(list, 1); content.Children.Add(list);
            var actions = new Grid { Margin = new Thickness(0, 12, 0, 0) }; actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); actions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            foreach (var button in new[] { addPreset, editPreset, deletePreset }) { button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Center; }
            addPreset.Margin = new Thickness(0); addPreset.Padding = new Thickness(10); Grid.SetColumnSpan(addPreset, 2); actions.Children.Add(addPreset);
            editPreset.Margin = new Thickness(0, 8, 8, 0); editPreset.Padding = new Thickness(8, 10, 8, 10); Grid.SetRow(editPreset, 1); actions.Children.Add(editPreset);
            deletePreset.Margin = new Thickness(0, 8, 0, 0); deletePreset.Padding = editPreset.Padding; Grid.SetColumn(deletePreset, 1); Grid.SetRow(deletePreset, 1); actions.Children.Add(deletePreset); Grid.SetRow(actions, 2); content.Children.Add(actions);
            presetStatus.Margin = new Thickness(0, 12, 0, 0); presetStatus.MinHeight = 42; presetStatus.MaxHeight = 64; presetStatus.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = presetStatus }); Grid.SetRow(presetStatus, 3); content.Children.Add(presetStatus);
            var card = Ui.Card(content, new Thickness(20)); System.Windows.Automation.AutomationProperties.SetName(card, "Saved times panel"); return card;
        }
        private void LoadPresets()
        {
            try { presetValues = preview ? ShutdownPresetStore.Defaults() : presetStore.Load(); presetsReady = true; ShowPresets(null); if (preview) Ui.Status(presetStatus, "Sample saved times. Adding, modifying and deleting are disabled."); else if (presetValues.Count == 0) Ui.Status(presetStatus, "No saved times. Use Add time to create one."); }
            catch (Exception error) { presetsReady = false; emptyPresets.Text = "Saved times unavailable."; Ui.Status(presetStatus, "Unable to load saved times: " + error.Message, true); }
        }
        private void ShowPresets(string selectedId)
        {
            bindingPresets = true; presets.ItemsSource = presetValues; presets.SelectedItem = presetValues.Find(value => value.Id == selectedId); bindingPresets = false;
            emptyPresets.Visibility = presetValues.Count == 0 ? Visibility.Visible : Visibility.Collapsed; if (presets.SelectedItem != null) presets.ScrollIntoView(presets.SelectedItem); UpdateControls();
        }
        private void ApplySelected()
        {
            if (busy || disposed || !presetsReady) return; var selected = presets.SelectedItem as ShutdownPreset; if (selected == null) return;
            if (scheduled.HasValue) { Ui.Status(presetStatus, "Cancel the active shutdown before loading another time."); return; }
            Duration.SetTotalSeconds(selected.Seconds); Ui.Status(presetStatus, "Time loaded. Press Schedule shutdown to start.");
        }
        private static ListBoxItem FindRow(DependencyObject source)
        { while (source != null) { var item = source as ListBoxItem; if (item != null) return item; source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source); } return null; }
        private static ShutdownPreset OpenEditor(Window owner, ShutdownPreset value)
        { var dialog = new ShutdownPresetWindow(owner, value); return dialog.ShowDialog() == true ? dialog.SavedPreset : null; }
        private async Task EditSavedTime(ShutdownPreset selected)
        {
            if (preview || busy || disposed || !presetsReady) return;
            busy = true; UpdateControls();
            try
            {
                var value = editor(Window.GetWindow(this), selected); if (value == null || disposed) return;
                if (selected != null) value = new ShutdownPreset(selected.Id, value.Name, value.Seconds);
                var next = presetValues.Where(item => selected == null || item.Id != selected.Id).ToList(); next.Add(value); var prepared = ShutdownPresetStore.Prepare(next);
                await Task.Run(() => presetStore.Save(prepared)); if (disposed) return; presetValues = prepared; ShowPresets(value.Id); Ui.Status(presetStatus, selected == null ? "Saved time added. Double-click it to load the timer." : "Saved time updated.");
            }
            catch (Exception error) { if (!disposed) Ui.Status(presetStatus, "Unable to save this time: " + error.Message, true); }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        private async Task DeleteSelectedTime()
        {
            if (preview || busy || disposed || !presetsReady) return; var selected = presets.SelectedItem as ShutdownPreset; if (selected == null) return;
            int index = presets.SelectedIndex; var next = presetValues.Where(value => value.Id != selected.Id).ToList(); busy = true; UpdateControls();
            try { await Task.Run(() => presetStore.Save(next)); if (disposed) return; presetValues = next; ShowPresets(next.Count == 0 ? null : next[Math.Min(index, next.Count - 1)].Id); Ui.Status(presetStatus, next.Count == 0 ? "No saved times. Use Add time to create one." : "Saved time deleted."); }
            catch (Exception error) { if (!disposed) Ui.Status(presetStatus, "Unable to delete this time: " + error.Message, true); }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        private async Task Schedule()
        {
            if (busy || scheduled.HasValue || disposed) return; int seconds = Duration.TotalSeconds;
            if (seconds == 0) { Ui.Status(status, "Choose a duration longer than zero.", true); return; }
            if (preview) { Ui.Status(status, "Preview only: no shutdown has been scheduled."); return; }
            busy = true; UpdateControls();
            try { await Task.Run(() => execute(ShutdownService.ScheduleArguments(seconds))); if (disposed) return; scheduled = DateTimeOffset.Now.AddSeconds(seconds); timer.Start(); UpdateCountdown(); }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Windows could not schedule shutdown: " + error.Message, true); }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        private async Task Cancel()
        {
            if (busy || disposed) return; if (preview) { Ui.Status(status, "Preview only: Windows timers were not changed."); return; }
            busy = true; UpdateControls();
            try { await Task.Run(() => execute(ShutdownService.CancelArguments)); if (disposed) return; scheduled = null; timer.Stop(); Ui.Status(status, "Shutdown cancelled."); }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Windows could not cancel shutdown: " + error.Message, true); }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        private void UpdateCountdown()
        { if (!scheduled.HasValue) return; var remaining = scheduled.Value - DateTimeOffset.Now; if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero; Ui.Status(status, "Shutting down in " + ((int)remaining.TotalHours).ToString("00") + ":" + remaining.Minutes.ToString("00") + ":" + remaining.Seconds.ToString("00")); }
        private void UpdateControls()
        {
            bool enabled = !busy && !disposed; start.IsEnabled = Duration.IsEnabled = enabled && !scheduled.HasValue; cancel.IsEnabled = enabled;
            presets.IsEnabled = enabled && presetsReady; addPreset.IsEnabled = enabled && presetsReady && !preview; editPreset.IsEnabled = deletePreset.IsEnabled = enabled && presetsReady && !preview && presets.SelectedItem != null;
        }
        public void Dispose() { disposed = true; timer.Stop(); UpdateControls(); }
    }
    internal sealed class DurationInput : StackPanel
    {
        internal readonly DurationField Hours = new DurationField(99, "Hours"), Minutes = new DurationField(59, "Minutes", 1), Seconds = new DurationField(59, "Seconds");
        internal int TotalSeconds { get { return Hours.Value * 3600 + Minutes.Value * 60 + Seconds.Value; } }
        internal void SetTotalSeconds(int seconds)
        {
            if (seconds < 0 || seconds > ShutdownPresetStore.MaximumSeconds) throw new ArgumentOutOfRangeException("seconds");
            Hours.SetValue(seconds / 3600); Minutes.SetValue(seconds % 3600 / 60); Seconds.SetValue(seconds % 60);
        }
        internal DurationInput()
        {
            Orientation = Orientation.Horizontal;
            Add(Hours, "HOURS"); Separator(); Add(Minutes, "MINUTES"); Separator(); Add(Seconds, "SECONDS");
        }
        private void Separator() { var separator = Ui.Text(":", 36, true); separator.Margin = new Thickness(12, 52, 12, 0); Children.Add(separator); }
        private void Add(DurationField field, string label)
        {
            var panel = new StackPanel { Width = 100 };
            var up = Ui.Button("\uE70E"); up.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"); up.Margin = new Thickness(0, 0, 0, 8); up.HorizontalAlignment = HorizontalAlignment.Stretch; up.HorizontalContentAlignment = HorizontalAlignment.Center; up.Appearance = global::Wpf.Ui.Controls.ControlAppearance.Transparent; up.Click += delegate { field.Step(1); }; panel.Children.Add(up);
            panel.Children.Add(field);
            var down = Ui.Button("\uE70D"); down.FontFamily = up.FontFamily; down.Margin = new Thickness(0, 8, 0, 8); down.HorizontalAlignment = HorizontalAlignment.Stretch; down.HorizontalContentAlignment = HorizontalAlignment.Center; down.Appearance = up.Appearance; down.Click += delegate { field.Step(-1); }; panel.Children.Add(down);
            var caption = Ui.Text(label, 11, true); caption.TextAlignment = TextAlignment.Center; panel.Children.Add(caption); Children.Add(panel);
        }
    }
    internal sealed class DurationField : global::Wpf.Ui.Controls.TextBox
    {
        private readonly int maximum;
        private bool fresh = true, updating;
        private string valid;
        internal int Value { get { return int.Parse(valid, CultureInfo.InvariantCulture); } }
        internal DurationField(int maximum, string name, int initial = 0)
        {
            this.maximum = maximum; valid = initial.ToString("00"); Text = valid;
            Style = (Style)Application.Current.FindResource(typeof(global::Wpf.Ui.Controls.TextBox));
            ClearButtonEnabled = false;
            ShowClearButton = false;
            FontSize = 38; FontWeight = FontWeights.SemiBold; TextAlignment = TextAlignment.Center; Padding = new Thickness(4, 8, 4, 8);
            System.Windows.Automation.AutomationProperties.SetName(this, name);
            GotKeyboardFocus += delegate { fresh = true; SelectAll(); };
            PreviewMouseLeftButtonDown += delegate { fresh = true; };
            PreviewTextInput += delegate(object sender, TextCompositionEventArgs e) { e.Handled = true; foreach (char digit in e.Text) PushDigit(digit); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.Key == Key.Up || e.Key == Key.Down) { Step(e.Key == Key.Up ? 1 : -1); e.Handled = true; } else if (e.Key == Key.Back || e.Key == Key.Delete) { SetValue(0); fresh = true; e.Handled = true; } };
            DataObject.AddPastingHandler(this, delegate(object sender, DataObjectPastingEventArgs e)
            { e.CancelCommand(); var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string; int number; if (text != null && text.Length <= 2 && text.Length > 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number <= maximum) { SetValue(number); fresh = true; } });
            TextChanged += delegate
            { if (updating) return; int value; if (Text.Length > 0 && Text.Length <= 2 && int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum) { valid = value.ToString("00"); } else SetValue(Value); };
            LostKeyboardFocus += delegate { SetValue(Value); fresh = true; };
        }
        internal void PushDigit(char digit)
        {
            if (digit < '0' || digit > '9') return;
            int next = (fresh ? 0 : Value % 10) * 10 + digit - '0';
            if (next > maximum) next = digit - '0';
            SetValue(next); fresh = false;
        }
        internal void Step(int change) { SetValue((Value + change + maximum + 1) % (maximum + 1)); fresh = true; }
        internal void SetValue(int number) { if (number < 0 || number > maximum) throw new ArgumentOutOfRangeException("number"); valid = number.ToString("00"); updating = true; Text = valid; CaretIndex = Text.Length; updating = false; fresh = true; }
    }
}
