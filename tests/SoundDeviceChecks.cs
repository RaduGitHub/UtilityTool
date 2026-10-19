using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UtilityTool.Backend;
#if !SOUND_DEVICE_BACKEND_ONLY
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UtilityTool.Frontend.Wpf;
#endif

internal static class SoundDeviceChecks
{
    internal static void RunBackend(Action<bool, string> check)
    {
        var primary = new Endpoint("speakers", "Speakers (Realtek Audio)", 1);
        var missing = new Endpoint("gone", "Disconnected endpoint", 1) { IdFails = true };
        var headphones = new Endpoint("headphones", "Headphones", 3) { FriendlyFails = true, Description = "Headphones fallback" };
        var monitor = new Endpoint("monitor", "Display audio", 9) { FormFails = true };
        var cable = new Endpoint("cable", "CABLE Input (VB-Audio Virtual Cable)", 1);
        var wideCable = new Endpoint("wide", "CABLE In 16ch (VB-Audio Virtual Cable)", 2);
        var namelessCable = new Endpoint("nameless-cable", "CABLE Input", 1) { FriendlyFails = true, Description = "VB-Audio Virtual Cable" };
        var endpoints = new[] { primary, missing, headphones, monitor, cable, wideCable, namelessCable };
        var enumerator = new Enumerator(primary, endpoints);
        var devices = WindowsAudio.GetOutputs(enumerator);
        check(devices.Count == 3 && devices.Single(device => device.Id == "speakers").IsDefault, "A disappearing endpoint does not remove healthy speakers, headphones or the Windows primary output");
        check(devices.Single(device => device.Id == "headphones").Name == "Headphones fallback" && devices.Single(device => device.Id == "headphones").FormFactor == 3, "A failed friendly-name property falls back to a readable description without losing the device");
        check(devices.Single(device => device.Id == "monitor").Name == "Display audio" && devices.Single(device => device.Id == "monitor").FormFactor == 1, "A failed icon property keeps the output with a speaker fallback");
        check(!devices.Any(device => device.Id == "cable" || device.Id == "wide" || device.Id == "nameless-cable"), "Virtual cables, including the 16-channel endpoint and a missing friendly name, stay out of speaker mirroring");
        check(enumerator.RenderOnly, "Sound device discovery queries render outputs only and does not activate capture or playback clients");
        var badDefault = new Endpoint("speakers", "Unavailable default", 1) { IdFails = true };
        devices = WindowsAudio.GetOutputs(new Enumerator(badDefault, new[] { primary, headphones }));
        check(devices.Count == 2 && !devices.Any(device => device.IsDefault), "An unavailable Windows default leaves physical outputs visible without inventing a primary");
        var collectionFailure = new Enumerator(primary, new[] { primary, headphones, monitor }) { FailingIndex = 1 };
        devices = WindowsAudio.GetOutputs(collectionFailure);
        check(devices.Select(device => device.Id).OrderBy(id => id).SequenceEqual(new[] { "monitor", "speakers" }), "One failed collection item leaves the remaining output list intact");
        check(!WindowsAudio.IsVirtualOutput("USB cable speakers") && WindowsAudio.IsVirtualOutput("Voicemeeter Input (VB-Audio Voicemeeter VAIO)"), "Virtual endpoint filtering does not hide ordinary speakers with cable in their name");
    }
#if !SOUND_DEVICE_BACKEND_ONLY
    // Live device metadata only: no IAudioClient activation, playback or capture.
    // Hold the first enumerator alive so this catches the shared COM-identity
    // conflict that isolated Sound and fake-session checks cannot exercise.
    internal static void RunInterop(Action<bool, string> check, bool naudioFirst)
    {
        IMMDeviceEnumerator custom = null;
        NAudio.CoreAudioApi.MMDeviceEnumerator sdk = null;
        try
        {
            if (naudioFirst) { sdk = new NAudio.CoreAudioApi.MMDeviceEnumerator(); custom = WindowsAudio.CreateEnumerator(); }
            else { custom = WindowsAudio.CreateEnumerator(); sdk = new NAudio.CoreAudioApi.MMDeviceEnumerator(); }
            check(custom != null && sdk != null, "NAudio and Sound enumerators coexist with " + (naudioFirst ? "Soundboard" : "Sound") + " initialized first");
            for (int repeat = 0; repeat < 5; repeat++)
            {
                var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var device in sdk.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                    using (device) if (!WindowsAudio.IsVirtualOutput(device.FriendlyName)) expected.Add(device.ID);
                var outputs = WindowsAudio.GetOutputs();
                check(expected.SetEquals(outputs.Select(device => device.Id)), "Physical output enumeration matches NAudio while both enumerators remain alive (pass " + (repeat + 1) + ")");
                var retained = WindowsAudio.GetOutputs(custom);
                check(expected.SetEquals(retained.Select(device => device.Id)), "The retained Sound enumerator remains usable after repeated shared activations (pass " + (repeat + 1) + ")");
            }
            sdk.Dispose(); sdk = null;
            check(WindowsAudio.GetOutputs(custom) != null, "Closing the NAudio enumerator leaves the retained Sound enumerator usable");
            WindowsAudio.Release(custom); custom = null;
            using (var reopened = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                WindowsAudio.GetOutputs();
                foreach (var device in reopened.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active)) device.Dispose();
                check(true, "Fresh Soundboard and Sound enumeration works after both original enumerators are disposed");
            }
        }
        finally { if (sdk != null) sdk.Dispose(); WindowsAudio.Release(custom); }
    }
    internal static void RunUi(Action<bool, string> check, Action pump)
    {
        var previous = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try { RunUiCore(check, pump); }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunUiCore(Action<bool, string> check, Action pump)
    {
        bool fail = true;
        var available = new List<AudioOutputDevice> { new AudioOutputDevice("speakers", "Speakers", true), new AudioOutputDevice("headphones", "Headphones", false, 3), new AudioOutputDevice("cable", "CABLE Input (VB-Audio Virtual Cable)", false) };
        using (var page = new SoundPage(false, () => { if (fail) throw new COMException("Temporary endpoint failure"); return available; }))
        {
            var fixture = new Window { Title = "Owned Sound list check", Width = 1040, Height = 740, ShowActivated = false, Content = page };
            fixture.Show();
            var first = page.LoadDevices(); Wait(first, pump);
            check(!first.Result && page.NeedsLoading && page.Tiles.Items.Count == 0 && Visuals<TextBlock>(page).Any(text => text.Visibility == Visibility.Visible && text.Text.Contains("Temporary endpoint failure") && text.Text.Contains("Refresh devices")), "A first Sound-load failure shows an explicit error and refresh action instead of a blank grid");
            fail = false; var retry = page.LoadDevices(); Wait(retry, pump);
            var tiles = page.Tiles.Items.Cast<TileRow>().SelectMany(row => row.Items).ToList();
            check(retry.Result && tiles.Count == 2 && tiles.Single(tile => tile.IsPrimary).Id == "speakers", "Refresh recovers the physical output grid while excluding the virtual cable");
            tiles.Single(tile => tile.Id == "headphones").Selected = true;
            fail = true; var failedRefresh = page.LoadDevices(); Wait(failedRefresh, pump);
            check(!failedRefresh.Result && page.Tiles.Items.Cast<TileRow>().SelectMany(row => row.Items).Count() == 2 && page.SecondaryIds().SequenceEqual(new[] { "headphones" }), "A transient refresh failure preserves visible devices and selected secondary outputs");
            fail = false; var recovered = page.LoadDevices(); Wait(recovered, pump);
            check(recovered.Result && page.SecondaryIds().SequenceEqual(new[] { "headphones" }), "A successful retry preserves available secondary selections");
            available = new List<AudioOutputDevice> { new AudioOutputDevice("speakers", "Speakers", false), new AudioOutputDevice("headphones", "Headphones", false, 3), new AudioOutputDevice("cable", "CABLE Input (VB-Audio Virtual Cable)", true) };
            var virtualDefault = page.LoadDevices(); Wait(virtualDefault, pump);
            tiles = page.Tiles.Items.Cast<TileRow>().SelectMany(row => row.Items).ToList();
            var start = Visuals<global::Wpf.Ui.Controls.Button>(page).Single(button => (string)button.Content == "Start multiple outputs");
            check(virtualDefault.Result && tiles.Count == 2 && !tiles.Any(tile => tile.IsPrimary) && !start.IsEnabled && Visuals<TextBlock>(page).Any(text => text.Text.Contains("Windows' default output")), "A virtual Windows default leaves physical tiles visible and explains how to choose a physical primary");
            available = new List<AudioOutputDevice>(); var empty = page.LoadDevices(); Wait(empty, pump);
            check(empty.Result && page.Tiles.Items.Count == 0 && Visuals<TextBlock>(page).Any(text => text.Visibility == Visibility.Visible && text.Text.StartsWith("No speakers or headphones found")), "An empty physical output list shows a clear connect-and-refresh state");
            fixture.Close();
        }
    }
    private static void Wait(Task task, Action pump)
    { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!task.IsCompleted && timer.ElapsedMilliseconds < 10000) { pump(); System.Threading.Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException("Sound device check timed out."); task.GetAwaiter().GetResult(); pump(); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T) yield return (T)root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
#endif
    private sealed class Enumerator : IMMDeviceEnumerator
    {
        private readonly Endpoint primary;
        private readonly Endpoint[] endpoints;
        internal int FailingIndex = -1;
        internal bool RenderOnly = true;
        internal Enumerator(Endpoint primary, Endpoint[] endpoints) { this.primary = primary; this.endpoints = endpoints; }
        public int EnumAudioEndpoints(int flow, uint state, out IMMDeviceCollection devices) { RenderOnly &= flow == 0 && state == 1; devices = new Collection(endpoints, FailingIndex); return 0; }
        public int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device) { RenderOnly &= flow == 0 && role == 1; device = primary; return 0; }
        public int GetDevice(string id, out IMMDevice device) { device = null; throw new Exception("Device activation is not part of enumeration checks."); }
        public int RegisterEndpointNotificationCallback(IntPtr callback) { return 0; }
        public int UnregisterEndpointNotificationCallback(IntPtr callback) { return 0; }
    }
    private sealed class Collection : IMMDeviceCollection
    {
        private readonly Endpoint[] endpoints;
        private readonly int failingIndex;
        internal Collection(Endpoint[] endpoints, int failingIndex) { this.endpoints = endpoints; this.failingIndex = failingIndex; }
        public int GetCount(out uint count) { count = (uint)endpoints.Length; return 0; }
        public int Item(uint index, out IMMDevice device) { device = null; if (index == failingIndex) return unchecked((int)0x80004005); device = endpoints[index]; return 0; }
    }
    private sealed class Endpoint : IMMDevice, IPropertyStore
    {
        private readonly string id, name;
        private readonly uint form;
        internal bool IdFails, FriendlyFails, FormFails;
        internal string Description;
        internal Endpoint(string id, string name, uint form) { this.id = id; this.name = name; this.form = form; }
        public int Activate(ref Guid iid, uint context, IntPtr parameters, out object instance) { instance = null; throw new Exception("No audio client may be activated in discovery checks."); }
        public int OpenPropertyStore(uint access, out IPropertyStore properties) { properties = this; return 0; }
        public int GetId(out string value) { value = id; return IdFails ? unchecked((int)0x80004005) : 0; }
        public int GetState(out uint state) { state = 1; return 0; }
        public int GetCount(out uint count) { count = 0; return 0; }
        public int GetAt(uint index, out PropertyKey key) { key = default(PropertyKey); return 0; }
        public int GetValue(ref PropertyKey key, out PropVariant value)
        {
            value = default(PropVariant);
            if (key.FormatId == new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"))
            {
                if (key.PropertyId == 14 && FriendlyFails) throw new COMException("Friendly name unavailable");
                string text = key.PropertyId == 14 ? name : Description;
                if (text != null) { value.Type = 31; value.Pointer = Marshal.StringToCoTaskMemUni(text); }
            }
            else
            {
                if (FormFails) throw new COMException("Form factor unavailable");
                value.Type = 19; value.UnsignedValue = form;
            }
            return 0;
        }
        public int SetValue(ref PropertyKey key, ref PropVariant value) { throw new Exception("Discovery cannot write device properties."); }
        public int Commit() { throw new Exception("Discovery cannot commit device properties."); }
    }
}
