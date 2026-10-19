using System;
using System.IO;
using System.Security.Cryptography;

namespace UtilityTool.Backend
{
    internal sealed class PasswordStore
    {
        private const int Iterations = 600000;
        private readonly string filePath;
        private byte[] salt;
        private byte[] hash;
        internal bool HasPassword { get { return hash != null; } }

        internal PasswordStore(string path, bool loadExisting = true)
        {
            filePath = path;
            if (!loadExisting || !File.Exists(path)) return;
            byte[] bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            try
            {
                if (bytes.Length != 64) throw new InvalidDataException("Invalid password settings.");
                salt = new byte[32]; hash = new byte[32];
                Buffer.BlockCopy(bytes, 0, salt, 0, 32);
                Buffer.BlockCopy(bytes, 32, hash, 0, 32);
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        internal void SetPassword(string password)
        {
            // Temporarily disabled for visual testing:
            // if (password == null || password.Length < 8 || password.Length > 256)
            //     throw new ArgumentException("Use a password between 8 and 256 characters.");
            if (password == null) throw new ArgumentNullException("password");
            var newSalt = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(newSalt);
            byte[] newHash = Derive(password, newSalt);
            byte[] bytes = new byte[64];
            Buffer.BlockCopy(newSalt, 0, bytes, 0, 32);
            Buffer.BlockCopy(newHash, 0, bytes, 32, 32);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                string temporary = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
                    if (File.Exists(filePath)) File.Replace(temporary, filePath, null);
                    else File.Move(temporary, filePath);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                salt = newSalt; hash = newHash;
            }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }

        internal bool Verify(string password)
        {
            // Password length limit temporarily disabled for visual testing.
            if (!HasPassword || password == null) return false;
            byte[] candidate = Derive(password, salt);
            try
            {
                int difference = 0;
                for (int index = 0; index < hash.Length; index++) difference |= hash[index] ^ candidate[index];
                return difference == 0;
            }
            finally { Array.Clear(candidate, 0, candidate.Length); }
        }

        private static byte[] Derive(string password, byte[] salt)
        {
            using (var derivation = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
                return derivation.GetBytes(32);
        }
    }
}
