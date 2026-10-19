using System;
using System.IO;
using System.Security;
using System.Xml;

namespace UtilityTool.Backend
{
    internal sealed class SoundboardSettings
    {
        internal float Volume = SoundboardAudioService.DefaultVolume;
        internal string ListeningOutputId;
        internal string MicrophoneId;
        internal string VirtualOutputId;
        internal bool CallAudioEnabled;
    }

    internal sealed class SoundboardSettingsStore
    {
        private readonly string path;
        private readonly object sync = new object();

        internal SoundboardSettingsStore(string filePath = null)
        { path = Path.GetFullPath(filePath ?? Path.Combine(new SoundboardStore().Root, "settings.xml")); }

        internal SoundboardSettings Load()
        {
            lock (sync)
            {
                var settings = new SoundboardSettings();
                if (!File.Exists(path)) return settings;
                try
                {
                    var document = new XmlDocument { XmlResolver = null };
                    using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 })) document.Load(reader);
                    var root = document.DocumentElement;
                    if (root == null || root.Name != "soundboardSettings" || root.GetAttribute("version") != "1") return settings;
                    float volume;
                    try { volume = XmlConvert.ToSingle(root.GetAttribute("volume")); }
                    catch (FormatException) { volume = SoundboardAudioService.DefaultVolume; }
                    catch (OverflowException) { volume = SoundboardAudioService.DefaultVolume; }
                    settings.Volume = ValidVolume(volume) ? volume : SoundboardAudioService.DefaultVolume;
                    settings.ListeningOutputId = DeviceId(root.GetAttribute("listeningOutputId"));
                    settings.MicrophoneId = DeviceId(root.GetAttribute("microphoneId"));
                    settings.VirtualOutputId = DeviceId(root.GetAttribute("virtualOutputId"));
                    try { settings.CallAudioEnabled = XmlConvert.ToBoolean(root.GetAttribute("callAudioEnabled")); }
                    catch (FormatException) { settings.CallAudioEnabled = false; }
                    return settings;
                }
                catch (Exception error) when (error is XmlException || error is IOException || error is UnauthorizedAccessException || error is SecurityException)
                { return new SoundboardSettings(); }
            }
        }

        internal void Save(SoundboardSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            lock (sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                    {
                        writer.WriteStartElement("soundboardSettings"); writer.WriteAttributeString("version", "1");
                        writer.WriteAttributeString("volume", XmlConvert.ToString(ValidVolume(settings.Volume) ? settings.Volume : SoundboardAudioService.DefaultVolume));
                        WriteDeviceId(writer, "listeningOutputId", settings.ListeningOutputId);
                        WriteDeviceId(writer, "microphoneId", settings.MicrophoneId);
                        WriteDeviceId(writer, "virtualOutputId", settings.VirtualOutputId);
                        writer.WriteAttributeString("callAudioEnabled", XmlConvert.ToString(settings.CallAudioEnabled));
                        writer.WriteEndElement();
                    }
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        private static bool ValidVolume(float value)
        { return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value <= 1; }

        private static string DeviceId(string value)
        { return string.IsNullOrWhiteSpace(value) || value.Length > 2048 ? null : value; }

        private static void WriteDeviceId(XmlWriter writer, string name, string value)
        { value = DeviceId(value); if (value != null) writer.WriteAttributeString(name, value); }
    }
}
