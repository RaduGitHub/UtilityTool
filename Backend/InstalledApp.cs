using System;
namespace UtilityTool.Backend
{
    internal sealed class InstalledApp
    {
        internal readonly string Id;
        internal readonly string Name;
        internal readonly string ExecutablePath;
        internal InstalledApp(string id, string name) : this(id, name, "") { }
        internal InstalledApp(string id, string name, string executablePath)
        { Id = id; Name = name; ExecutablePath = executablePath; }
        internal bool CanGuard { get { return !string.IsNullOrEmpty(ExecutablePath) || Id.IndexOf('!') > 0; } }
        public override string ToString() { return Name + (CanGuard ? "" : " (guard unavailable)"); }
    }


}
