using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UtilityTool.Backend;

internal static class SoundboardAssetImportChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), "reduced-import-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string audio = Path.Combine(root, "original.mp3"), image = Path.Combine(root, "large.bmp");
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        var tone = new OffsetSampleProvider(new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.04 }) { Take = TimeSpan.FromMilliseconds(300) };
        MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(tone), audio, 128000);
        using (var bitmap = new Bitmap(3000, 2000, PixelFormat.Format24bppRgb))
        { using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue); bitmap.Save(image, ImageFormat.Bmp); }
        byte[] originalAudio = File.ReadAllBytes(audio), originalImage = File.ReadAllBytes(image);
        var store = new SoundboardStore(Path.Combine(root, "library")); bool rejected = false;
        try { store.Import("Original size", audio, image); } catch (ArgumentException error) { rejected = error.Message.Contains("Reduce file size"); }
        check(rejected && originalImage.Length > 15 * 1024 * 1024 && !Directory.Exists(store.Root), "A large original image shows the reduction option without partially creating a sound");
        var sound = store.Import("Reduced image", audio, image, true);
        using (var saved = Image.FromFile(sound.ImagePath))
            check(saved.Width <= 512 && saved.Height <= 512 && Math.Abs(saved.Width / (double)saved.Height - 1.5) < 0.01 && new FileInfo(sound.ImagePath).Length < 15 * 1024 * 1024, "Opt-in import accepts an image over 15 MB and saves artwork with its aspect ratio within 512 pixels");
        check(File.ReadAllBytes(sound.AudioPath).SequenceEqual(originalAudio) && File.ReadAllBytes(audio).SequenceEqual(originalAudio) && File.ReadAllBytes(image).SequenceEqual(originalImage), "Reducing artwork preserves an already-small MP3 and both user-selected source files byte-for-byte");
        string oldImage = sound.ImagePath, oldAudio = sound.AudioPath;
        sound = store.Update(sound.Id, "Replaced image", image: image, reduceFileSize: true);
        check(sound.ImagePath != oldImage && !File.Exists(oldImage) && sound.AudioPath == oldAudio && File.ReadAllBytes(oldAudio).SequenceEqual(originalAudio) && Directory.GetFiles(Path.GetDirectoryName(oldAudio)).Length == 2, "An optimized artwork replacement removes the previous owned image and retains the unchanged MP3");
        using (var unavailable = new FileStream(oldAudio, FileMode.Open, FileAccess.Read, FileShare.None))
            sound = store.Update(sound.Id, "Name only", reduceFileSize: true);
        check(sound.Name == "Name only" && sound.AudioPath == oldAudio, "A name-only edit with reduction selected still avoids opening or replacing existing audio");
        string catalog = Path.Combine(store.Root, "sounds.xml"), bad = Path.Combine(root, "invalid.png"); File.WriteAllText(bad, "not an image");
        byte[] priorCatalog = File.ReadAllBytes(catalog), priorImage = File.ReadAllBytes(sound.ImagePath); string[] files = Directory.GetFiles(Path.GetDirectoryName(oldAudio)); rejected = false;
        try { store.Update(sound.Id, "Failed reduction", image: bad, reduceFileSize: true); } catch { rejected = true; }
        check(rejected && File.ReadAllBytes(catalog).SequenceEqual(priorCatalog) && File.ReadAllBytes(sound.ImagePath).SequenceEqual(priorImage) && Directory.GetFiles(Path.GetDirectoryName(oldAudio)).OrderBy(value => value).SequenceEqual(files.OrderBy(value => value)), "An image-conversion failure preserves the prior catalog and all saved assets");
        rejected = false;
        using (var blocked = new FileStream(catalog, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { store.Update(sound.Id, "Blocked reduction", image: image, reduceFileSize: true); } catch (IOException) { rejected = true; }
        check(rejected && File.ReadAllBytes(catalog).SequenceEqual(priorCatalog) && File.ReadAllBytes(sound.ImagePath).SequenceEqual(priorImage) && Directory.GetFiles(Path.GetDirectoryName(oldAudio)).Length == 2, "A failed catalog commit removes optimized replacement staging files and leaves the original saved sound intact");
        check(File.ReadAllBytes(image).SequenceEqual(originalImage) && File.ReadAllBytes(audio).SequenceEqual(originalAudio), "Successful and failed optimized imports never modify the original image or MP3");
    }
}
