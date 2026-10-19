using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class ShutdownPresetChecks
{
    internal static void Run(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            string root = Path.Combine(Path.GetFullPath(output), "shutdown-presets-" + Guid.NewGuid().ToString("N"));
            StoreChecks(check, root); PageChecks(check, root, owner, pump, capture); EditorChecks(check, root, owner, pump, capture);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void StoreChecks(Action<bool, string> check, string root)
    {
        string path = Path.Combine(root, "storage", "shutdown-presets.xml"); var store = new ShutdownPresetStore(path); var defaults = store.Load();
        check(defaults.Select(item => item.Seconds).SequenceEqual(new[] { 300, 900, 1800 }) && defaults.Select(item => item.Name).SequenceEqual(new[] { "5 minutes", "15 minutes", "30 minutes" }) && defaults.All(item => IsId(item.Id)) && defaults.Select(item => item.Id).Distinct().Count() == 3 && !Directory.Exists(root), "Missing settings return named first-use presets with stable IDs without creating files");
        check(defaults.Select(item => item.Id).SequenceEqual(store.Load().Select(item => item.Id)), "Default preset identities are stable across reads");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        const string legacy = "<shutdownPresets version=\"1\"><preset seconds=\"1800\"/><preset seconds=\"300\"/><preset seconds=\"3661\"/></shutdownPresets>"; File.WriteAllText(path, legacy);
        var migrated = store.Load();
        check(migrated.Select(item => item.Seconds).SequenceEqual(new[] { 300, 1800, 3661 }) && migrated.All(item => IsId(item.Id) && !string.IsNullOrWhiteSpace(item.Name)) && migrated.Select(item => item.Id).SequenceEqual(store.Load().Select(item => item.Id)) && File.ReadAllText(path) == legacy, "Version 1 durations migrate to deterministic named identities without rewriting the original file on read");
        store.Save(migrated); var reloaded = new ShutdownPresetStore(path).Load();
        check(File.ReadAllText(path).Contains("version=\"2\"") && Equivalent(migrated, reloaded), "Saving migrated presets writes version 2 and preserves every ID, name and duration");
        var first = Preset("Evening", 900); var second = Preset("Tea break", 900); store.Save(new[] { first, second });
        check(store.Load().Count == 2 && store.Load().All(item => item.Seconds == 900), "Different named presets may share the same duration");
        var renamed = new ShutdownPreset(first.Id, "After dinner", 3661); store.Save(new[] { renamed, second });
        check(store.Load().Single(item => item.Id == first.Id).Name == "After dinner" && store.Load().Single(item => item.Id == first.Id).Seconds == 3661, "Changing a preset name and duration preserves its identity");
        store.Save(new ShutdownPreset[0]); check(store.Load().Count == 0 && File.Exists(path), "An explicitly saved empty preset list remains empty after reopening");
        store.Save(new[] { Preset("Shortest", 1), Preset("Longest", ShutdownPresetStore.MaximumSeconds) }); byte[] intact = File.ReadAllBytes(path);
        var invalid = new Func<ShutdownPreset[]>[] {
            () => new[] { new ShutdownPreset("not-an-id", "Bad ID", 300) },
            () => new[] { Preset("", 300) }, () => new[] { Preset("   ", 300) }, () => new[] { Preset(new string('x', 65), 300) },
            () => new[] { Preset("Zero", 0) }, () => new[] { Preset("Negative", -1) }, () => new[] { Preset("Too long", ShutdownPresetStore.MaximumSeconds + 1) },
            () => new[] { first, new ShutdownPreset(first.Id, "Another", 100) },
            () => new[] { first, new ShutdownPreset(Guid.NewGuid().ToString("N"), first.Name, first.Seconds) }
        };
        foreach (var values in invalid)
        {
            bool rejected = false; try { store.Save(values()); } catch (ArgumentException) { rejected = true; }
            check(rejected && File.ReadAllBytes(path).SequenceEqual(intact), "Invalid preset identity, name, duration or duplicate is rejected without modifying saved presets");
        }
        foreach (string xml in new[] { "<broken", "<shutdownPresets version=\"3\"/>", "<shutdownPresets version=\"1\"><preset seconds=\"0\"/></shutdownPresets>", "<shutdownPresets version=\"1\"><preset seconds=\"300\"/><preset seconds=\"300\"/></shutdownPresets>", "<shutdownPresets version=\"2\"><preset id=\"bad\" name=\"Bad\" seconds=\"300\"/></shutdownPresets>", "<!DOCTYPE shutdownPresets [<!ENTITY x '300'>]><shutdownPresets version=\"1\"><preset seconds=\"&x;\"/></shutdownPresets>" })
        {
            File.WriteAllText(path, xml); bool rejected = false; try { store.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(path) == xml, "Malformed, unsupported or unsafe preset XML is rejected and preserved for recovery");
        }
        File.WriteAllBytes(path, intact);
        using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            bool rejected = false; try { store.Save(new[] { Preset("Blocked", 600) }); } catch (IOException) { rejected = true; }
            check(rejected && File.ReadAllBytes(path).SequenceEqual(intact) && Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "A blocked replacement retains the original XML and removes temporary save files");
        }
    }
    private static void PageChecks(Action<bool, string> check, string root, Window owner, Action pump, Action<Window, string> capture)
    {
        string path = Path.Combine(root, "page", "shutdown-presets.xml"); var store = new ShutdownPresetStore(path); var commands = new ConcurrentQueue<string>();
        var edits = new Queue<ShutdownPreset>(); var editInputs = new List<ShutdownPreset>();
        Func<Window, ShutdownPreset, ShutdownPreset> editor = (window, existing) => { editInputs.Add(existing); return edits.Count == 0 ? null : edits.Dequeue(); };
        using (var page = new ShutdownPage(false, store, commands.Enqueue, editor))
        {
            var window = Host(owner, page); pump(); var list = Presets(page); var add = Button(page, "Add time"); var modify = Button(page, "Modify selected"); var remove = Button(page, "Delete selected");
            check(list.Items.Count == 3 && page.Duration.TotalSeconds == 60 && !File.Exists(path) && commands.IsEmpty, "First-use named presets leave the one-minute timer and Windows shutdown untouched");
            double[] minimum = LayoutCheck(check, window, page, list, "minimum"); capture(window, "Shutdown-presets-minimum");
            window.Width = 1520; window.Height = 940; pump(); double[] wide = LayoutCheck(check, window, page, list, "wide");
            check(wide[0] >= minimum[0] && wide[1] > minimum[1] + 100, "Widening the window grows the timer pane while preserving usable preset-list width"); capture(window, "Shutdown-presets-wide"); window.Width = 1040; window.Height = 740; pump();
            list.SelectedIndex = 1;
            check(page.Duration.TotalSeconds == 60 && commands.IsEmpty && modify.IsEnabled && remove.IsEnabled, "A single preset selection only selects it for management and leaves the timer unchanged");
            list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = list });
            check(page.Duration.TotalSeconds == 60 && commands.IsEmpty, "Double-clicking empty preset-list space does not apply an unrelated selection");
            DoubleClick(list);
            check(page.Duration.TotalSeconds == 900 && commands.IsEmpty, "Double-clicking a preset applies its duration without scheduling shutdown");
            list.SelectedIndex = 0; Enter(list);
            check(page.Duration.TotalSeconds == 300 && commands.IsEmpty, "Enter applies the selected preset without scheduling shutdown");
            page.Duration.SetTotalSeconds(731); Click(add); pump();
            check(editInputs.Last() == null && page.Duration.TotalSeconds == 731 && list.Items.Count == 3 && !File.Exists(path), "Add time opens an independent blank editor and cancelling leaves the running timer and list unchanged");
            var added = Preset("After the film", 3661); edits.Enqueue(added); Click(add); WaitFor(() => add.IsEnabled && list.Items.Count == 4, pump);
            check(Equivalent(store.Load(), list.Items.Cast<ShutdownPreset>().ToList()) && page.Duration.TotalSeconds == 731 && list.Items.Cast<ShutdownPreset>().Any(item => item.Id == added.Id), "Adding a named duration persists it without applying it to the timer");
            list.SelectedItem = list.Items.Cast<ShutdownPreset>().Single(item => item.Id == added.Id); Click(modify); pump();
            check(editInputs.Last().Id == added.Id && page.Duration.TotalSeconds == 731 && list.Items.Cast<ShutdownPreset>().Single(item => item.Id == added.Id).Name == added.Name, "Modify selected supplies the existing preset and cancellation preserves both preset and timer");
            var edited = new ShutdownPreset(added.Id, "After the credits with a very long saved shutdown time name", 359999); edits.Enqueue(edited); Click(modify); WaitFor(() => modify.IsEnabled && list.Items.Cast<ShutdownPreset>().Any(item => item.Name == edited.Name), pump); pump();
            check(store.Load().Single(item => item.Id == added.Id).Seconds == ShutdownPresetStore.MaximumSeconds && page.Duration.TotalSeconds == 731, "Editing persists a new name and the maximum duration while preserving preset identity and timer input");
            check(Visuals<TextBlock>(list).Any(value => value.Text == edited.Name && value.TextTrimming == TextTrimming.CharacterEllipsis && string.Equals(value.ToolTip as string, edited.Name, StringComparison.Ordinal)), "Long saved names use ellipsis and retain the full name in a tooltip"); capture(window, "Shutdown-presets-named");
            DoubleClick(list); check(page.Duration.TotalSeconds == ShutdownPresetStore.MaximumSeconds && page.Duration.Hours.Text == "99" && page.Duration.Minutes.Text == "59" && page.Duration.Seconds.Text == "59", "Applying the edited maximum duration fills all three existing timer fields");
            page.Duration.Minutes.PushDigit('7'); check(page.Duration.Minutes.Value == 7, "Applying a preset resets rolling entry so the next minute digit starts at the lower place");
            page.Duration.SetTotalSeconds(90); Click(Button(page, "Schedule shutdown")); WaitFor(() => commands.Count == 1 && Button(page, "Cancel shutdown").IsEnabled, pump);
            int before = page.Duration.TotalSeconds; list.SelectedIndex = 0; DoubleClick(list); Enter(list);
            check(commands.Single() == "/s /t 90" && page.Duration.TotalSeconds == before && !page.Duration.IsEnabled && add.IsEnabled && modify.IsEnabled && remove.IsEnabled, "Scheduling alone invokes the command; preset management stays available while applying durations is blocked");
            var whileScheduled = Preset("Later", 42); edits.Enqueue(whileScheduled); Click(add); WaitFor(() => add.IsEnabled && list.Items.Cast<ShutdownPreset>().Any(item => item.Id == whileScheduled.Id), pump);
            check(page.Duration.TotalSeconds == before && commands.Count == 1, "Adding a preset while scheduled does not change the active countdown or issue another command");
            Click(Button(page, "Cancel shutdown")); WaitFor(() => commands.Count == 2 && page.Duration.IsEnabled, pump);
            check(commands.Last() == "/a" && Button(page, "Schedule shutdown").IsEnabled, "Cancel invokes only the injected cancellation command and restores timer input");
            while (list.Items.Count > 0) { int count = list.Items.Count - 1; list.SelectedIndex = 0; Click(remove); WaitFor(() => add.IsEnabled && list.Items.Count == count, pump); }
            check(store.Load().Count == 0 && !modify.IsEnabled && !remove.IsEnabled && commands.Count == 2, "Deleting every named preset persists an empty list without executing Windows commands"); window.Close();
        }
        using (var page = new ShutdownPage(false, store, commands.Enqueue, editor))
        {
            var window = Host(owner, page); pump(); var list = Presets(page); var add = Button(page, "Add time");
            check(list.Items.Count == 0 && Status(page, "No saved times"), "Reopening preserves an empty preset list with visible guidance");
            var original = Preset("Original", 90); edits.Enqueue(original); Click(add); WaitFor(() => add.IsEnabled && list.Items.Count == 1, pump); list.SelectedIndex = 0; byte[] intact = File.ReadAllBytes(path);
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                edits.Enqueue(Preset("Unsaved", 42)); Click(add); WaitFor(() => add.IsEnabled && Status(page, "Unable to save"), pump);
                check(list.Items.Count == 1 && File.ReadAllBytes(path).SequenceEqual(intact), "A failed Add save keeps the previous visible presets and XML");
                edits.Enqueue(new ShutdownPreset(original.Id, "Unsaved edit", 600)); Click(Button(page, "Modify selected")); WaitFor(() => Button(page, "Modify selected").IsEnabled && Status(page, "Unable to save"), pump);
                check(list.Items.Cast<ShutdownPreset>().Single().Name == original.Name && File.ReadAllBytes(path).SequenceEqual(intact), "A failed Modify save rolls back the visible name and duration");
                Click(Button(page, "Delete selected")); WaitFor(() => Button(page, "Delete selected").IsEnabled && Status(page, "Unable to delete"), pump);
                check(list.Items.Count == 1 && File.ReadAllBytes(path).SequenceEqual(intact), "A failed Delete save retains the selected preset and restores its controls");
            }
            window.Close();
        }
        using (var page = new ShutdownPage(false, store, argument => { throw new InvalidOperationException("Owned rejected command"); }, editor))
        {
            var window = Host(owner, page); pump(); Click(Button(page, "Schedule shutdown")); WaitFor(() => Status(page, "Owned rejected command") && page.Duration.IsEnabled, pump);
            check(Button(page, "Add time").IsEnabled && Presets(page).IsEnabled, "A scheduling failure leaves both the timer and independent preset management usable"); window.Close();
        }
        string badPath = Path.Combine(root, "preview", "shutdown-presets.xml"); Directory.CreateDirectory(Path.GetDirectoryName(badPath)); const string malformed = "Owned unreadable settings"; File.WriteAllText(badPath, malformed); int commandCount = commands.Count, editorCount = editInputs.Count;
        using (var page = new ShutdownPage(true, new ShutdownPresetStore(badPath), commands.Enqueue, editor))
        {
            var window = Host(owner, page); pump(); var list = Presets(page); list.SelectedIndex = 0;
            check(list.Items.Count == 3 && !Button(page, "Add time").IsEnabled && !Button(page, "Modify selected").IsEnabled && !Button(page, "Delete selected").IsEnabled, "Preview shows read-only sample named presets without reading damaged user settings");
            foreach (string name in new[] { "Add time", "Modify selected", "Delete selected", "Schedule shutdown", "Cancel shutdown" }) Button(page, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); pump();
            check(commands.Count == commandCount && editInputs.Count == editorCount && File.ReadAllText(badPath) == malformed, "Preview guards reject CRUD and shutdown execution even when disabled action events are raised directly"); window.Close();
        }
        using (var page = new ShutdownPage(false, new ShutdownPresetStore(badPath), commands.Enqueue, editor))
        {
            var window = Host(owner, page); pump(); check(Status(page, "Unable to load saved times") && !Button(page, "Add time").IsEnabled && Button(page, "Schedule shutdown").IsEnabled && page.Duration.IsEnabled, "Damaged presets show recovery guidance while preserving the ordinary timer"); window.Close();
        }
    }
    private static void EditorChecks(Action<bool, string> check, string root, Window owner, Action pump, Action<Window, string> capture)
    {
        var original = Preset("Evening shutdown", 3661); ShutdownPreset saved = null;
        OpenEditor(owner, null, pump, dialog =>
        {
            var name = Visuals<System.Windows.Controls.TextBox>(dialog).Single(field => System.Windows.Automation.AutomationProperties.GetName(field) == "Preset name");
            check(name.Text.Length == 0 && dialog.Duration.TotalSeconds > 0, "The Add editor starts with an empty name and its own positive duration");
            name.Text = "   "; dialog.Duration.SetTotalSeconds(60); Button(dialog, "Save time").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); pump(); check(dialog.IsVisible && dialog.SavedPreset == null, "The editor rejects an empty preset name without closing");
            name.Text = "Valid name"; dialog.Duration.SetTotalSeconds(0); Button(dialog, "Save time").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); pump(); check(dialog.IsVisible && dialog.SavedPreset == null && !Status(dialog, "Parameter name"), "The editor rejects zero duration with a friendly message and remains open");
            name.Text = "  After music  "; dialog.Duration.SetTotalSeconds(3723); check(!Status(dialog, "Choose a time between"), "Editing the name or duration clears stale validation feedback"); capture(dialog, "Shutdown-add-time"); Click(Button(dialog, "Save time")); saved = dialog.SavedPreset;
        });
        check(saved != null && IsId(saved.Id) && saved.Name == "After music" && saved.Seconds == 3723, "Saving a valid editor trims the name and returns an independent named duration");
        OpenEditor(owner, original, pump, dialog =>
        {
            var name = Visuals<System.Windows.Controls.TextBox>(dialog).Single(field => System.Windows.Automation.AutomationProperties.GetName(field) == "Preset name");
            check(name.Text == original.Name && dialog.Duration.TotalSeconds == original.Seconds, "Modify opens a prefilled independent editor"); name.Text = "Cancelled change"; dialog.Duration.SetTotalSeconds(42); capture(dialog, "Shutdown-modify-time"); Click(Button(dialog, "Cancel"));
            check(dialog.SavedPreset == null && original.Name == "Evening shutdown" && original.Seconds == 3661, "Cancelling the editor preserves the immutable source preset");
        });
        OpenEditor(owner, original, pump, dialog =>
        {
            var name = Visuals<System.Windows.Controls.TextBox>(dialog).Single(field => System.Windows.Automation.AutomationProperties.GetName(field) == "Preset name"); name.Text = "Renamed"; dialog.Duration.SetTotalSeconds(7200); Click(Button(dialog, "Save time"));
            check(dialog.SavedPreset != null && dialog.SavedPreset.Id == original.Id && dialog.SavedPreset.Name == "Renamed" && dialog.SavedPreset.Seconds == 7200, "Saving a modification preserves the preset ID while updating name and duration");
        });
    }
    private static double[] LayoutCheck(Action<bool, string> check, Window window, ShutdownPage page, ListBox list, string label)
    {
        page.UpdateLayout(); var timerPane = Visuals<FrameworkElement>(page).FirstOrDefault(value => System.Windows.Automation.AutomationProperties.GetName(value) == "Shutdown timer panel");
        var durationOrigin = page.Duration.TranslatePoint(new Point(0, 0), window); var listOrigin = list.TranslatePoint(new Point(0, 0), window);
        var firstField = page.Duration.Hours.TranslatePoint(new Point(0, 0), window); var lastField = page.Duration.Seconds.TranslatePoint(new Point(0, 0), window);
        double visibleFieldCenter = (firstField.X + lastField.X + page.Duration.Seconds.ActualWidth) / 2;
        bool centered = timerPane != null && Math.Abs(visibleFieldCenter - timerPane.TranslatePoint(new Point(0, 0), window).X - timerPane.ActualWidth / 2) < 2;
        bool buttonsFit = new[] { "Add time", "Modify selected", "Delete selected", "Schedule shutdown", "Cancel shutdown" }.Select(name => Button(page, name)).All(button => { var origin = button.TranslatePoint(new Point(0, 0), window); return origin.X >= 0 && origin.Y >= 0 && origin.X + button.ActualWidth <= window.ActualWidth && origin.Y + button.ActualHeight <= window.ActualHeight; });
        check(listOrigin.X + list.ActualWidth <= durationOrigin.X && centered && buttonsFit && list.ActualHeight >= 100, "The preset list remains left of a centered right-hand timer and all preset/timer buttons fit at " + label + " window size");
        return new[] { list.ActualWidth, timerPane == null ? 0 : timerPane.ActualWidth };
    }
    private static void OpenEditor(Window owner, ShutdownPreset preset, Action pump, Action<ShutdownPresetWindow> action)
    {
        var dialog = new ShutdownPresetWindow(owner, preset); Exception failure = null; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += delegate { if (!dialog.IsVisible) return; timer.Stop(); try { action(dialog); } catch (Exception error) { failure = error; dialog.Close(); } }; timer.Start();
        try { dialog.ShowDialog(); if (failure != null) throw failure; }
        finally { timer.Stop(); }
    }
    private static ShutdownPreset Preset(string name, int seconds) { return new ShutdownPreset(Guid.NewGuid().ToString("N"), name, seconds); }
    private static bool IsId(string value) { Guid parsed; return Guid.TryParseExact(value, "N", out parsed); }
    private static bool Equivalent(IList<ShutdownPreset> first, IList<ShutdownPreset> second) { return first.Count == second.Count && first.All(item => second.Any(other => other.Id == item.Id && other.Name == item.Name && other.Seconds == item.Seconds)); }
    private static Window Host(Window owner, ShutdownPage page)
    {
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) }); layout.RowDefinitions.Add(new RowDefinition()); Grid.SetRow(page, 1); layout.Children.Add(page);
        var window = new global::Wpf.Ui.Controls.FluentWindow { Owner = owner, Width = 1040, Height = 740, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Content = layout, Background = owner.Background, Foreground = Brushes.WhiteSmoke }; window.Closed += delegate { page.Dispose(); }; window.Show(); return window;
    }
    private static ListBox Presets(DependencyObject root) { return Visuals<ListBox>(root).Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "Saved shutdown times"); }
    private static Button Button(DependencyObject root, string label) { return Visuals<Button>(root).Single(button => button.Content as string == label); }
    private static bool Status(DependencyObject root, string text) { return Visuals<TextBlock>(root).Any(label => label.Text.Contains(text)); }
    private static void Click(Button button) { if (!button.IsEnabled) throw new Exception("Fixture attempted a disabled preset action: " + button.Content); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static void DoubleClick(ListBox list) { list.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) ?? list }); }
    private static void Enter(ListBox list) { list.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(list), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); }
    private static void WaitFor(Func<bool> done, Action pump) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!done() && clock.ElapsedMilliseconds < 10000) { pump(); Thread.Sleep(5); } if (!done()) throw new TimeoutException("Shutdown preset check timed out."); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { if (root is T) yield return (T)root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child; }
}
