using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UtilityTool.Backend
{
    // Squirrel apps move their EXE to app-<version> after each update.
    // Match just that executable in the selected installation, not other apps.
    internal sealed class VersionedAppExecutable
    {
        internal readonly string Directory, Name;
        private VersionedAppExecutable(string directory, string name) { Directory = directory; Name = name; }
        internal static VersionedAppExecutable From(InstalledApp app)
        {
            if (string.IsNullOrEmpty(app.ExecutablePath) || !Path.IsPathRooted(app.ExecutablePath)) return null;
            string name = Path.GetFileName(app.ExecutablePath), root = Path.GetDirectoryName(app.ExecutablePath);
            if (string.Equals(name, "Update.exe", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(app.LaunchArguments ?? "", "(?:^|\\s)--processStart(?:AndWait)?(?:=|\\s+)(?:\"([^\"]+)\"|([^\\s]+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!match.Success) return null;
                name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (Path.GetFileName(name) != name || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ||
                    !string.Equals(Path.GetExtension(name), ".exe", StringComparison.OrdinalIgnoreCase)) return null;
            }
            else if (IsVersion(Path.GetFileName(root))) root = Path.GetDirectoryName(root);
            if (string.IsNullOrEmpty(root) || !File.Exists(Path.Combine(root, "Update.exe"))) return null;
            return new VersionedAppExecutable(Path.GetFullPath(root), name);
        }
        private static bool IsVersion(string name)
        { Version version; return name != null && name.StartsWith("app-", StringComparison.OrdinalIgnoreCase) && Version.TryParse(name.Substring(4), out version); }
        internal bool Matches(string path)
        {
            if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path) || !string.Equals(Path.GetFileName(path), Name, StringComparison.OrdinalIgnoreCase)) return false;
            string parent = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.Equals(parent, Directory, StringComparison.OrdinalIgnoreCase) ||
                (IsVersion(Path.GetFileName(parent)) && string.Equals(Path.GetDirectoryName(parent), Directory, StringComparison.OrdinalIgnoreCase));
        }
        internal List<string> Existing()
        {
            var paths = new List<string>();
            string main = Path.Combine(Directory, Name); if (File.Exists(main)) paths.Add(main);
            try
            {
                foreach (string version in System.IO.Directory.GetDirectories(Directory, "app-*", SearchOption.TopDirectoryOnly))
                {
                    string executable = Path.Combine(version, Name);
                    if (IsVersion(Path.GetFileName(version)) && File.Exists(executable)) paths.Add(executable);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return paths;
        }
    }
}
