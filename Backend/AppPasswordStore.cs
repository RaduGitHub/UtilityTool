using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UtilityTool.Backend
{
    // The page authenticates with the UtilityTool password before changing this store.
    internal sealed class AppPasswordStore
    {
        private readonly string root;
        private readonly PasswordStore utilityPassword;
        private readonly object cacheGate = new object();
        private readonly Dictionary<string, PasswordStore> cache = new Dictionary<string, PasswordStore>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> mutationGates = new Dictionary<string, object>(StringComparer.Ordinal);
        internal AppPasswordStore(string rootDirectory, PasswordStore utilityPassword)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Choose a folder for app passwords.", "rootDirectory");
            if (utilityPassword == null) throw new ArgumentNullException("utilityPassword");
            root = Path.GetFullPath(rootDirectory); this.utilityPassword = utilityPassword;
        }
        internal bool HasOverride(InstalledApp app) { return File.Exists(PathFor(app)); }
        internal PasswordStore Resolve(InstalledApp app)
        {
            string path = PathFor(app);
            lock (cacheGate)
            {
                if (!File.Exists(path)) { cache.Remove(path); return utilityPassword; }
                PasswordStore password;
                if (!cache.TryGetValue(path, out password)) { password = new PasswordStore(path); cache[path] = password; }
                return password;
            }
        }
        internal void Set(InstalledApp app, string password)
        {
            if (password == null) throw new ArgumentNullException("password");
            string path = PathFor(app);
            lock (MutationGate(path))
            {
                // Do not mutate the cached store before the atomic file replacement succeeds.
                // A caller already showing a prompt must recreate it after changing this rule.
                // Master authentication was completed by the caller. A damaged old
                // override must not prevent replacing it with a newly protected hash.
                var candidate = new PasswordStore(path, false); candidate.SetPassword(password);
                lock (cacheGate) cache[path] = candidate;
            }
        }
        internal void UseUtilityPassword(InstalledApp app)
        {
            string path = PathFor(app);
            lock (MutationGate(path))
            {
                // Delete only this service's hashed file, never the folder or the master password.
                if (File.Exists(path)) File.Delete(path);
                lock (cacheGate) cache.Remove(path);
            }
        }
        private object MutationGate(string path)
        {
            lock (cacheGate) { object gate; if (!mutationGates.TryGetValue(path, out gate)) { gate = new object(); mutationGates[path] = gate; } return gate; }
        }
        private string PathFor(InstalledApp app)
        {
            if (app == null) throw new ArgumentNullException("app");
            if (string.IsNullOrWhiteSpace(app.Id)) throw new ArgumentException("The app does not have a stable identity.", "app");
            byte[] identity = Encoding.UTF8.GetBytes(app.Id.ToUpperInvariant());
            try
            {
                using (var sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(identity); var name = new StringBuilder(64);
                    foreach (byte value in hash) name.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    return Path.Combine(root, name + ".dat");
                }
            }
            finally { Array.Clear(identity, 0, identity.Length); }
        }
    }
}
