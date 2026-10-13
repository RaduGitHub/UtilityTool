using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Xml;
using NAudio.Wave;

namespace UtilityTool.Backend
{
    internal sealed class SoundboardSound
    {
        internal string Id { get; private set; }
        internal string Name { get; private set; }
        internal string AudioPath { get; private set; }
        internal string ImagePath { get; private set; }
        internal SoundboardSound(string id, string name, string audio, string image)
        { Id = id; Name = name; AudioPath = audio; ImagePath = image; }
    }

    // The library owns its copies. Originals are never changed or deleted.
    internal sealed class SoundboardStore
    {
        internal string Root { get; private set; }
        private string Catalog { get { return Path.Combine(Root, "sounds.xml"); } }
        internal SoundboardStore(string root = null)
        { Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UtilityTool", "Soundboard")); }

        internal List<SoundboardSound> Load()
        {
            var result = new List<SoundboardSound>();
            if (!File.Exists(Catalog)) return result;
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(Catalog, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4000000 })) document.Load(reader);
            var root = document.DocumentElement;
            if (root == null || root.Name != "soundboard" || root.GetAttribute("version") != "1") throw new InvalidDataException("The sound library could not be read. Your sound files are still saved.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement item in root.SelectNodes("sound"))
            {
                string id = item.GetAttribute("id"), name = item.GetAttribute("name");
                Guid guid;
                if (!Guid.TryParseExact(id, "N", out guid) || !ids.Add(id) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("The sound library contains an invalid entry.");
                string audio = item.GetAttribute("audio"), image = item.GetAttribute("image");
                if (!IsAudioName(audio) || (image.Length > 0 && !IsImageName(image))) throw new InvalidDataException("The sound library contains an invalid file path.");
                result.Add(new SoundboardSound(id, name, Asset(id, audio), image.Length == 0 ? null : Asset(id, image)));
            }
            return result;
        }

        private static readonly string[] ImageNames = { "image.png", "image.jpg", "image.jpeg", "image.bmp", "image.gif", "image.ico" };
        private static bool IsAudioName(string file) { return file == "audio.mp3" || IsGeneratedName(file, "audio", ".mp3"); }
        private static bool IsImageName(string file)
        { return ImageNames.Contains(file) || ImageNames.Any(name => IsGeneratedName(file, "image", name.Substring(5))); }
        private static bool IsGeneratedName(string file, string prefix, string extension)
        {
            Guid guid; string start = prefix + "-";
            return file.Length == start.Length + 32 + extension.Length && file.StartsWith(start, StringComparison.Ordinal) && file.EndsWith(extension, StringComparison.Ordinal) && Guid.TryParseExact(file.Substring(start.Length, 32), "N", out guid);
        }
        private string Asset(string id, string file) { return Path.Combine(Root, "assets", id, file); }

        internal SoundboardSound Import(string name, string mp3, string image = null, bool reduceFileSize = false)
        {
            if (!reduceFileSize) return ImportPrepared(name, mp3, image);
            ValidateName(name); ValidateAudioSource(mp3);
            using (var prepared = SoundboardAssetOptimizer.Prepare(mp3, image))
                return ImportPrepared(name, prepared.AudioPath, prepared.ImagePath);
        }

        private SoundboardSound ImportPrepared(string name, string mp3, string image)
        {
            name = ValidateName(name); ValidateAudioSource(mp3);
            string imageName = null;
            if (!string.IsNullOrWhiteSpace(image))
            {
                imageName = "image" + Path.GetExtension(image).ToLowerInvariant();
                if (!File.Exists(image) || !ImageNames.Contains(imageName)) throw new ArgumentException("Choose a PNG, JPG, BMP, GIF or ICO image.");
                if (new FileInfo(image).Length > 15 * 1024 * 1024) throw new ArgumentException("This image exceeds 15 MB. Enable Reduce file size or choose a smaller image.");
                using (var decoded = Image.FromFile(image))
                    if ((long)decoded.Width * decoded.Height > 64000000) throw new ArgumentException("This image exceeds 64 megapixels. Enable Reduce file size or choose a smaller image.");
            }
            var sounds = Load();
            string id = Guid.NewGuid().ToString("N"), folder = Path.Combine(Root, "assets", id);
            Directory.CreateDirectory(folder);
            var sound = new SoundboardSound(id, name, Asset(id, "audio.mp3"), imageName == null ? null : Asset(id, imageName));
            try
            {
                File.Copy(mp3, sound.AudioPath);
                // Validate the owned copy, so a changed source cannot invalidate this import.
                ValidateAudio(sound.AudioPath);
                if (imageName != null) File.Copy(image, sound.ImagePath);
                sounds.Add(sound); Save(sounds); return sound;
            }
            catch { DeleteAssets(sound); throw; }
        }

        internal SoundboardSound Update(string id, string name, string mp3 = null, string image = null, bool reduceFileSize = false)
        {
            if (!reduceFileSize || (mp3 == null && image == null)) return UpdatePrepared(id, name, mp3, image);
            ValidateName(name);
            if (!Load().Any(sound => sound.Id == id)) throw new ArgumentException("This sound no longer exists in your library.");
            if (mp3 != null) ValidateAudioSource(mp3);
            using (var prepared = SoundboardAssetOptimizer.Prepare(mp3, image))
                return UpdatePrepared(id, name, prepared.AudioPath, prepared.ImagePath);
        }

        private SoundboardSound UpdatePrepared(string id, string name, string mp3, string image)
        {
            name = ValidateName(name);
            var sounds = Load(); int index = sounds.FindIndex(sound => sound.Id == id);
            if (index < 0) throw new ArgumentException("This sound no longer exists in your library.");
            var previous = sounds[index];
            // A rename needs no decoder or asset writes, including for unavailable audio.
            if (mp3 == null && image == null)
            {
                var renamed = new SoundboardSound(previous.Id, name, previous.AudioPath, previous.ImagePath);
                sounds[index] = renamed; Save(sounds); return renamed;
            }

            if (mp3 != null) ValidateAudioSource(mp3);
            string imageExtension = image == null ? null : ValidateImageSource(image);
            string directory = Path.GetDirectoryName(previous.AudioPath);
            Directory.CreateDirectory(directory);
            string audioPath = mp3 == null ? previous.AudioPath : Asset(previous.Id, "audio-" + Guid.NewGuid().ToString("N") + ".mp3");
            string imagePath = image == null ? previous.ImagePath : Asset(previous.Id, "image-" + Guid.NewGuid().ToString("N") + imageExtension);
            var staged = new List<string>(); var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            byte[] catalog = File.ReadAllBytes(Catalog); bool committed = false;
            try
            {
                // New filenames keep the currently committed library intact while
                // sources are copied and decoded, even when a source is an old asset.
                if (mp3 != null) { staged.Add(audioPath); File.Copy(mp3, audioPath); File.SetAttributes(audioPath, FileAttributes.Normal); ValidateAudio(audioPath); }
                if (image != null) { staged.Add(imagePath); File.Copy(image, imagePath); File.SetAttributes(imagePath, FileAttributes.Normal); ValidateImage(imagePath); }
                foreach (string oldFile in new[] { mp3 == null ? null : previous.AudioPath, image == null ? null : previous.ImagePath })
                {
                    if (oldFile == null || !File.Exists(oldFile)) continue;
                    string backup = oldFile + "." + Guid.NewGuid().ToString("N") + ".backup";
                    backups.Add(oldFile, backup); File.Copy(oldFile, backup); File.SetAttributes(backup, FileAttributes.Normal);
                }
                var updated = new SoundboardSound(previous.Id, name, audioPath, imagePath);
                sounds[index] = updated; Save(sounds); committed = true;
                foreach (string oldFile in backups.Keys) File.Delete(oldFile);
                return updated;
            }
            catch
            {
                if (committed)
                {
                    // For example, a file may be busy during cleanup. Restore the
                    // exact previous metadata and any old copy already removed.
                    foreach (var backup in backups)
                        if (!File.Exists(backup.Key)) File.Copy(backup.Value, backup.Key);
                    RestoreCatalog(catalog); committed = false;
                }
                throw;
            }
            finally
            {
                if (!committed) foreach (string file in staged) if (File.Exists(file)) File.Delete(file);
                foreach (string file in backups.Values) if (File.Exists(file)) File.Delete(file);
            }
        }

        private static string ValidateName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 64) throw new ArgumentException("Enter a sound name between 1 and 64 characters.");
            return name;
        }
        private static void ValidateAudioSource(string path)
        { if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an MP3 file on your computer."); }
        private static void ValidateAudio(string path)
        {
            using (var decoded = new MediaFoundationReader(path))
                if (decoded.Length == 0 || decoded.Read(new byte[4096], 0, 4096) == 0) throw new InvalidDataException("This MP3 contains no playable audio.");
        }
        private static string ValidateImageSource(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (!File.Exists(path) || !ImageNames.Contains("image" + extension)) throw new ArgumentException("Choose a PNG, JPG, BMP, GIF or ICO image.");
            return extension;
        }
        private static void ValidateImage(string path)
        {
            if (new FileInfo(path).Length > 15 * 1024 * 1024) throw new ArgumentException("This image exceeds 15 MB. Enable Reduce file size or choose a smaller image.");
            using (var decoded = Image.FromFile(path))
                if ((long)decoded.Width * decoded.Height > 64000000) throw new ArgumentException("This image exceeds 64 megapixels. Enable Reduce file size or choose a smaller image.");
        }
        private void RestoreCatalog(byte[] catalog)
        {
            string temporary = Catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, catalog);
                if (File.Exists(Catalog)) File.Replace(temporary, Catalog, null); else File.Move(temporary, Catalog);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal void Delete(string id)
        {
            var sounds = Load(); var sound = sounds.Find(item => item.Id == id);
            if (sound == null) return;
            sounds.Remove(sound); Save(sounds); DeleteAssets(sound);
        }

        private void DeleteAssets(SoundboardSound sound)
        {
            // Only known files inside this library's generated GUID folder are removed.
            foreach (string file in new[] { sound.AudioPath, sound.ImagePath })
                if (file != null && File.Exists(file)) File.Delete(file);
            string directory = Path.GetDirectoryName(sound.AudioPath);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }

        private void Save(List<SoundboardSound> sounds)
        {
            Directory.CreateDirectory(Root); string temporary = Catalog + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true }))
                {
                    writer.WriteStartElement("soundboard"); writer.WriteAttributeString("version", "1");
                    foreach (var sound in sounds)
                    {
                        writer.WriteStartElement("sound"); writer.WriteAttributeString("id", sound.Id); writer.WriteAttributeString("name", sound.Name);
                        writer.WriteAttributeString("audio", Path.GetFileName(sound.AudioPath));
                        if (sound.ImagePath != null) writer.WriteAttributeString("image", Path.GetFileName(sound.ImagePath));
                        writer.WriteEndElement();
                    }
                    writer.WriteEndElement();
                }
                if (File.Exists(Catalog)) File.Replace(temporary, Catalog, null); else File.Move(temporary, Catalog);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
