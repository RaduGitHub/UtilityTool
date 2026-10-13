using System;
using System.Globalization;
using System.IO;
using System.Linq;
using UtilityTool.Backend;

internal static class SoundboardSettingsChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), "soundboard-settings-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "library", "settings.xml");
        var store = new SoundboardSettingsStore(path);
        var defaults = store.Load();
        check(IsDefault(defaults) && !Directory.Exists(root), "Reading absent soundboard preferences returns 25% volume with call audio off and creates no files");
        var settings = new SoundboardSettings { Volume = 0.37f, ListeningOutputId = "speakers<&>\"", MicrophoneId = "microphone-id", VirtualOutputId = "virtual-output-id", CallAudioEnabled = true };
        store.Save(settings);
        var restored = new SoundboardSettingsStore(path).Load();
        check(restored.Volume == settings.Volume && restored.ListeningOutputId == settings.ListeningOutputId && restored.MicrophoneId == settings.MicrophoneId && restored.VirtualOutputId == settings.VirtualOutputId && restored.CallAudioEnabled, "Volume, listening output, microphone, virtual output and call-audio state round-trip through escaped XML");
        string firstXml = File.ReadAllText(path);
        settings.Volume = 0; settings.CallAudioEnabled = false; settings.ListeningOutputId = null; store.Save(settings);
        restored = store.Load();
        check(restored.Volume == 0 && !restored.CallAudioEnabled && restored.ListeningOutputId == null && firstXml != File.ReadAllText(path), "Updating soundboard preferences atomically replaces an existing file and persists mute, cleared output and disabled call audio");
        settings.Volume = 1; store.Save(settings);
        check(store.Load().Volume == 1 && Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length == 0, "Maximum volume persists and successful saves leave no temporary settings files");

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ro-RO"); settings.Volume = 0.42f; store.Save(settings);
            check(store.Load().Volume == 0.42f && File.ReadAllText(path).Contains("volume=\"0.42\""), "Soundboard volume is saved and read independently of the Windows decimal separator");
        }
        finally { CultureInfo.CurrentCulture = culture; }

        foreach (string value in new[] { "-0.1", "1.1", "NaN", "INF", "-INF", "not-a-number", "", "1e99" })
        {
            File.WriteAllText(path, "<soundboardSettings version=\"1\" volume=\"" + value + "\" microphoneId=\"retained-microphone\" callAudioEnabled=\"true\" />");
            restored = store.Load();
            check(restored.Volume == SoundboardAudioService.DefaultVolume && restored.MicrophoneId == "retained-microphone" && restored.CallAudioEnabled, "Invalid stored volume '" + value + "' falls back to 25% while retaining valid preferences");
        }

        foreach (string xml in new[] { "<broken", "<other version=\"1\" />", "<soundboardSettings version=\"2\" volume=\"0.9\" callAudioEnabled=\"true\" />", "<!DOCTYPE soundboardSettings [<!ENTITY x '0.9'>]><soundboardSettings version=\"1\" volume=\"&x;\" />", new string(' ', 65537) })
        {
            File.WriteAllText(path, xml); restored = store.Load();
            check(IsDefault(restored) && File.ReadAllText(path) == xml, "Malformed, unsupported or unsafe soundboard settings return defaults without modifying the source file");
        }
        File.WriteAllText(path, "<soundboardSettings version=\"1\" volume=\"0.5\" listeningOutputId=\"  \" microphoneId=\"mic\" callAudioEnabled=\"invalid\" />");
        restored = store.Load();
        check(restored.Volume == 0.5f && restored.ListeningOutputId == null && restored.MicrophoneId == "mic" && restored.VirtualOutputId == null && !restored.CallAudioEnabled, "Missing or invalid individual settings safely default while valid volume and microphone remain");

        settings = new SoundboardSettings { Volume = float.NaN, MicrophoneId = new string('x', 2049), VirtualOutputId = "virtual-id" }; store.Save(settings);
        restored = store.Load();
        check(restored.Volume == SoundboardAudioService.DefaultVolume && restored.MicrophoneId == null && restored.VirtualOutputId == "virtual-id", "Saving invalid volume and oversized device identities normalizes them to safe defaults");
        string intact = File.ReadAllText(path); settings.MicrophoneId = "invalid\u0001xml";
        bool failed = false; try { store.Save(settings); } catch (ArgumentException) { failed = true; }
        check(failed && File.ReadAllText(path) == intact && !Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Any(), "A failed settings write preserves the previous complete XML and removes its temporary file");
    }

    private static bool IsDefault(SoundboardSettings settings)
    { return settings.Volume == SoundboardAudioService.DefaultVolume && settings.ListeningOutputId == null && settings.MicrophoneId == null && settings.VirtualOutputId == null && !settings.CallAudioEnabled; }
}
