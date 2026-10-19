using System;
using System.Collections.Generic;
using System.IO;

namespace UtilityTool.Backend
{
    internal static class AppGuardTargets
    {
        internal static List<string> Executables(InstalledApp app, string commonData = null)
        {
            var targets = new List<string>();
            if (string.IsNullOrEmpty(app.ExecutablePath)) return targets;
            var versioned = VersionedAppExecutable.From(app);
            if (versioned != null) return versioned.Existing();
            if (string.Equals(Path.GetFileName(app.ExecutablePath), "steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                targets.Add(app.ExecutablePath);
                string cef = Path.Combine(Path.GetDirectoryName(app.ExecutablePath), "bin", "cef");
                try
                {
                    if (Directory.Exists(cef)) foreach (string cefDirectory in Directory.GetDirectories(cef, "cef.*", SearchOption.TopDirectoryOnly))
                    {
                        string helper = Path.Combine(cefDirectory, "steamwebhelper.exe");
                        if (File.Exists(helper)) targets.Add(helper);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return targets;
            }
            bool league = string.Equals(Path.GetFileName(app.ExecutablePath), "RiotClientServices.exe", StringComparison.OrdinalIgnoreCase) &&
                ((app.LaunchArguments ?? "").IndexOf("--launch-product=league_of_legends", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 string.Equals(app.Name, "League of Legends", StringComparison.OrdinalIgnoreCase));
            if (!league) { targets.Add(app.ExecutablePath); return targets; }

            // League's shortcut points to the shared Riot launcher, not its UI.
            // Resolve only League executables so other Riot products stay independent.
            string patchline = (app.LaunchArguments ?? "").IndexOf("--launch-patchline=pbe", StringComparison.OrdinalIgnoreCase) >= 0 ? "pbe" : "live";
            string metadata = Path.Combine(commonData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Riot Games", "Metadata", "league_of_legends." + patchline, "league_of_legends." + patchline + ".product_settings.yaml");
            string directory = null;
            try
            {
                if (File.Exists(metadata)) foreach (string line in File.ReadLines(metadata))
                {
                    const string key = "product_install_full_path:";
                    if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
                    string value = line.Substring(key.Length).Trim().Trim('"', '\'');
                    if (Path.IsPathRooted(value)) directory = Path.GetFullPath(value);
                    break;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            if (directory == null)
            {
                string launcherDirectory = Path.GetDirectoryName(app.ExecutablePath);
                string parent = string.IsNullOrEmpty(launcherDirectory) ? null : Path.GetDirectoryName(launcherDirectory);
                if (!string.IsNullOrEmpty(parent))
                    directory = Path.Combine(parent, patchline == "pbe" ? "League of Legends PBE" : "League of Legends");
            }
            if (directory != null) foreach (string relative in new[] { "LeagueClient.exe", "LeagueClientUx.exe", Path.Combine("Game", "League of Legends.exe") })
            {
                string path = Path.Combine(directory, relative);
                if (File.Exists(path)) targets.Add(path);
            }
            if (targets.Count == 0) throw new InvalidOperationException("Could not locate League of Legends. Start the game once through Riot Client, then refresh the apps list and apply its lock again.");
            return targets;
        }
    }
}
