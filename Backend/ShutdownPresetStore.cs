using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;

namespace UtilityTool.Backend
{
    internal sealed class ShutdownPreset
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public int Seconds { get; private set; }
        public string DurationLabel { get { return (Seconds / 3600).ToString("00") + ":" + (Seconds % 3600 / 60).ToString("00") + ":" + (Seconds % 60).ToString("00"); } }
        internal ShutdownPreset(string id, string name, int seconds) { Id = id; Name = name; Seconds = seconds; }
    }
    internal sealed class ShutdownPresetStore
    {
        internal const int MaximumSeconds = 99 * 3600 + 59 * 60 + 59;
        internal const int MaximumNameLength = 64;
        private readonly string path;
        internal ShutdownPresetStore(string filePath = null)
        { path = Path.GetFullPath(filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UtilityTool", "shutdown-presets.xml")); }
        internal static List<ShutdownPreset> Defaults() { return new[] { 300, 900, 1800 }.Select(LegacyPreset).ToList(); }
        private static ShutdownPreset LegacyPreset(int seconds)
        {
            var parts = new List<string>(); int hours = seconds / 3600, minutes = seconds % 3600 / 60, remainder = seconds % 60;
            if (hours > 0) parts.Add(hours + (hours == 1 ? " hour" : " hours")); if (minutes > 0) parts.Add(minutes + (minutes == 1 ? " minute" : " minutes")); if (remainder > 0) parts.Add(remainder + (remainder == 1 ? " second" : " seconds"));
            // Legacy records had no identity. Keep the same identity across
            // reads until the next edit saves the named version 2 format.
            return new ShutdownPreset("8c662836e6224aefb0b6c7a0" + seconds.ToString("x8", CultureInfo.InvariantCulture), string.Join(" ", parts), seconds);
        }
        internal List<ShutdownPreset> Load()
        {
            if (!File.Exists(path)) return Defaults();
            var document = new XmlDocument { XmlResolver = null };
            try { using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4000000 })) document.Load(reader); }
            catch (XmlException error) { throw new InvalidDataException("Saved shutdown times could not be read. The settings file has been left unchanged.", error); }
            var root = document.DocumentElement;
            if (root == null || root.Name != "shutdownPresets" || (root.GetAttribute("version") != "1" && root.GetAttribute("version") != "2")) throw new InvalidDataException("Saved shutdown times use an unsupported settings format.");
            bool legacy = root.GetAttribute("version") == "1"; var values = new List<ShutdownPreset>();
            try
            {
                foreach (XmlElement item in root.SelectNodes("preset")) { int seconds = XmlConvert.ToInt32(item.GetAttribute("seconds")); values.Add(legacy ? LegacyPreset(seconds) : new ShutdownPreset(item.GetAttribute("id"), item.GetAttribute("name"), seconds)); }
                return Prepare(values);
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is OverflowException)
            { throw new InvalidDataException("A saved shutdown time is invalid. The settings file has been left unchanged.", error); }
        }
        internal static ShutdownPreset Normalize(ShutdownPreset value)
        {
            if (value == null) throw new ArgumentException("Choose a saved time.");
            Guid id; if (!Guid.TryParseExact(value.Id, "N", out id)) throw new ArgumentException("This saved time has an invalid identity.");
            string name = (value.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > MaximumNameLength || name.Any(char.IsControl)) throw new ArgumentException("Enter a name between 1 and 64 characters, using a single line.");
            if (value.Seconds < 1 || value.Seconds > MaximumSeconds) throw new ArgumentOutOfRangeException("value", "Choose a time between 00:00:01 and 99:59:59.");
            return new ShutdownPreset(id.ToString("N"), name, value.Seconds);
        }
        internal static List<ShutdownPreset> Prepare(IEnumerable<ShutdownPreset> values)
        {
            if (values == null) throw new ArgumentNullException("values");
            var snapshot = values.Select(Normalize).ToList(); var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var namesAndTimes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in snapshot)
            {
                if (!ids.Add(value.Id)) throw new ArgumentException("A saved time is listed more than once.");
                if (!namesAndTimes.Add(value.Name + "\0" + value.Seconds.ToString(CultureInfo.InvariantCulture))) throw new ArgumentException("A saved time with this name and duration already exists.");
            }
            return snapshot.OrderBy(value => value.Seconds).ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Id, StringComparer.Ordinal).ToList();
        }
        internal void Save(IEnumerable<ShutdownPreset> values)
        {
            var snapshot = Prepare(values); Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("shutdownPresets"); writer.WriteAttributeString("version", "2");
                    foreach (var value in snapshot) { writer.WriteStartElement("preset"); writer.WriteAttributeString("id", value.Id); writer.WriteAttributeString("name", value.Name); writer.WriteAttributeString("seconds", XmlConvert.ToString(value.Seconds)); writer.WriteEndElement(); }
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
