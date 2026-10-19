using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
namespace UtilityTool.Backend
{
    internal sealed class LockState
    {
        internal bool Locked;
        internal readonly List<InstalledApp> Apps = new List<InstalledApp>();
    }
    internal sealed class LockStateStore
    {
        private readonly string path;
        internal LockStateStore(string filePath) { path = filePath; }
        internal LockState Load()
        {
            var state = new LockState();
            if (!File.Exists(path)) return state;
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.Name != "lockState" || root.GetAttribute("version") != "1") throw new InvalidDataException("Invalid lock settings.");
            state.Locked = XmlConvert.ToBoolean(root.GetAttribute("locked"));
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in root.ChildNodes)
            {
                var item = node as XmlElement;
                if (item == null || item.Name != "app") continue;
                string id = item.GetAttribute("id");
                if (!string.IsNullOrWhiteSpace(id) && ids.Add(id))
                    state.Apps.Add(new InstalledApp(id, item.GetAttribute("name"), item.GetAttribute("executable"), item.GetAttribute("arguments")));
            }
            return state;
        }
        internal void Save(LockState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("lockState"); writer.WriteAttributeString("version", "1");
                    writer.WriteAttributeString("locked", XmlConvert.ToString(state.Locked));
                    foreach (var app in state.Apps)
                    {
                        writer.WriteStartElement("app"); writer.WriteAttributeString("id", app.Id);
                        writer.WriteAttributeString("name", app.Name); writer.WriteAttributeString("executable", app.ExecutablePath);
                        if (!string.IsNullOrEmpty(app.LaunchArguments)) writer.WriteAttributeString("arguments", app.LaunchArguments);
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
