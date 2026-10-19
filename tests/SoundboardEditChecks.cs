using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UtilityTool.Backend;

internal static class SoundboardEditChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), "soundboard-edits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string firstAudio = Path.Combine(root, "first.mp3"), secondAudio = Path.Combine(root, "second.mp3");
        string firstImage = Path.Combine(root, "first.png"), secondImage = Path.Combine(root, "second.png"), jpg = Path.Combine(root, "replacement.jpg");
        NAudio.MediaFoundation.MediaFoundationApi.Startup(); CreateTone(firstAudio, 440); CreateTone(secondAudio, 880);
        CreateImage(firstImage, Color.CornflowerBlue, ImageFormat.Png); CreateImage(secondImage, Color.Gold, ImageFormat.Png); CreateImage(jpg, Color.Purple, ImageFormat.Jpeg);
        byte[] firstAudioBytes = File.ReadAllBytes(firstAudio), secondAudioBytes = File.ReadAllBytes(secondAudio), firstImageBytes = File.ReadAllBytes(firstImage), secondImageBytes = File.ReadAllBytes(secondImage), jpgBytes = File.ReadAllBytes(jpg);
        var store = new SoundboardStore(Path.Combine(root, "library"));
        var sound = store.Import("Original", firstAudio, firstImage); string id = sound.Id;
        check(Path.GetFileName(sound.AudioPath) == "audio.mp3" && Path.GetFileName(sound.ImagePath) == "image.png" && new SoundboardStore(store.Root).Load().Single().Id == id, "Editing supports existing libraries using audio.mp3 and image.png");
        string oldAudio = sound.AudioPath, oldImage = sound.ImagePath;
        using (var lockedAudio = new FileStream(sound.AudioPath, FileMode.Open, FileAccess.Read, FileShare.None))
            sound = store.Update(id, "  Renamed & sound  ");
        check(sound.Id == id && sound.Name == "Renamed & sound" && sound.AudioPath == oldAudio && sound.ImagePath == oldImage && File.ReadAllBytes(oldAudio).SequenceEqual(firstAudioBytes) && File.ReadAllBytes(oldImage).SequenceEqual(firstImageBytes), "Name-only editing preserves ID and exact audio/artwork bytes and never opens the decoder");

        sound = store.Update(id, "Replaced", secondAudio, secondImage);
        check(sound.Id == id && sound.Name == "Replaced" && sound.AudioPath != oldAudio && sound.ImagePath != oldImage && File.ReadAllBytes(sound.AudioPath).SequenceEqual(secondAudioBytes) && File.ReadAllBytes(sound.ImagePath).SequenceEqual(secondImageBytes), "Replacing MP3 and artwork stages owned copies and retains the sound ID");
        check(!File.Exists(oldAudio) && !File.Exists(oldImage) && OwnedFiles(sound).Length == 2, "A committed edit removes replaced owned files and leaves no backups or staging copies");
        var restored = new SoundboardStore(store.Root).Load().Single();
        check(restored.Id == id && restored.Name == sound.Name && restored.AudioPath == sound.AudioPath && restored.ImagePath == sound.ImagePath, "Edited names and generated asset paths survive library reload");
        using (var decoded = new MediaFoundationReader(restored.AudioPath))
            check(decoded.Read(new byte[4096], 0, 4096) > 0, "The replacement is a real playable MP3");

        oldImage = sound.ImagePath; oldAudio = sound.AudioPath;
        using (var lockedAudio = new FileStream(sound.AudioPath, FileMode.Open, FileAccess.Read, FileShare.None))
            sound = store.Update(id, "JPEG artwork", image: jpg);
        check(sound.AudioPath == oldAudio && File.ReadAllBytes(sound.AudioPath).SequenceEqual(secondAudioBytes) && Path.GetExtension(sound.ImagePath) == ".jpg" && File.ReadAllBytes(sound.ImagePath).SequenceEqual(jpgBytes) && !File.Exists(oldImage) && OwnedFiles(sound).Length == 2, "Artwork-only editing supports a different extension without decoding or replacing the MP3");
        oldAudio = sound.AudioPath; oldImage = sound.ImagePath;
        sound = store.Update(id, "Reuse owned assets", sound.AudioPath, sound.ImagePath);
        check(sound.Id == id && File.ReadAllBytes(sound.AudioPath).SequenceEqual(secondAudioBytes) && File.ReadAllBytes(sound.ImagePath).SequenceEqual(jpgBytes) && !File.Exists(oldAudio) && !File.Exists(oldImage) && OwnedFiles(sound).Length == 2, "Existing owned MP3 and artwork can be replacement sources because copies are staged before removing the old assets");
        oldImage = sound.ImagePath;
        sound = store.Update(id, "Audio only", firstAudio);
        check(sound.ImagePath == oldImage && File.ReadAllBytes(sound.AudioPath).SequenceEqual(firstAudioBytes) && File.ReadAllBytes(sound.ImagePath).SequenceEqual(jpgBytes) && OwnedFiles(sound).Length == 2, "Null artwork retains the current image when replacing only the MP3");

        string badAudio = Path.Combine(root, "invalid.mp3"), badImage = Path.Combine(root, "invalid.png"); File.WriteAllText(badAudio, "not an MP3"); File.WriteAllText(badImage, "not an image");
        CheckFailedEdit(check, store, sound, () => store.Update(id, "Invalid audio", badAudio, firstImage), "An invalid replacement MP3 leaves the saved name and both assets byte-identical");
        CheckFailedEdit(check, store, sound, () => store.Update(id, "Invalid image", secondAudio, badImage), "An invalid image rolls back a valid staged MP3 and leaves the saved sound byte-identical");
        CheckFailedEdit(check, store, sound, () => store.Update(id, "Missing audio", Path.Combine(root, "missing.mp3")), "A missing replacement MP3 leaves the library unchanged");
        CheckFailedEdit(check, store, sound, () => store.Update(id, "Missing image", image: Path.Combine(root, "missing.png")), "A missing replacement image leaves the library unchanged");
        CheckFailedEdit(check, store, sound, () => store.Update(id, " ", secondAudio), "An empty replacement name leaves the library unchanged");
        CheckFailedEdit(check, store, sound, () => store.Update(id, new string('x', 65)), "An overlong replacement name leaves the library unchanged");
        CheckFailedEdit(check, store, sound, () => store.Update(Guid.NewGuid().ToString("N"), "Unknown", secondAudio), "Editing an unknown sound ID does not create a new entry or owned files");

        string catalog = Path.Combine(store.Root, "sounds.xml");
        using (var lockedCatalog = new FileStream(catalog, FileMode.Open, FileAccess.Read, FileShare.Read))
            CheckFailedEdit(check, store, sound, () => store.Update(id, "Failed commit", secondAudio, secondImage), "A blocked atomic catalog commit removes staged replacements and preserves the previous catalog and assets");
        File.SetAttributes(sound.ImagePath, FileAttributes.ReadOnly);
        try
        {
            CheckFailedEdit(check, store, sound, () => store.Update(id, "Failed cleanup", secondAudio, secondImage), "A failed old-asset cleanup restores already removed audio and the exact previous XML without leaving backups");
            check((File.GetAttributes(sound.ImagePath) & FileAttributes.ReadOnly) != 0, "Rolling back an edit preserves the existing artwork's read-only attribute");
        }
        finally { File.SetAttributes(sound.ImagePath, FileAttributes.Normal); }

        string validXml = File.ReadAllText(catalog), validAudioName = Path.GetFileName(sound.AudioPath);
        foreach (string invalidName in new[] { "audio-not-a-guid.mp3", "audio-" + new string('f', 32) + ".wav", "../audio-" + new string('f', 32) + ".mp3" })
        {
            File.WriteAllText(catalog, validXml.Replace(validAudioName, invalidName)); bool rejected = false;
            try { store.Load(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Generated replacement filenames reject invalid identities, extensions and folder traversal");
        }
        File.WriteAllText(catalog, validXml);
        check(!validXml.Contains(root) && validXml.Contains("audio-") && validXml.Contains("image-"), "Edited asset metadata remains portable and contains only generated leaf filenames");

        var withoutImage = store.Import("No artwork", secondAudio);
        withoutImage = store.Update(withoutImage.Id, "Added artwork", image: firstImage);
        check(withoutImage.ImagePath != null && File.ReadAllBytes(withoutImage.ImagePath).SequenceEqual(firstImageBytes) && OwnedFiles(withoutImage).Length == 2, "Editing can add artwork to a sound that originally had none");
        store.Delete(withoutImage.Id); string assetFolder = Path.GetDirectoryName(sound.AudioPath); store.Delete(id);
        check(store.Load().Count == 0 && !Directory.Exists(assetFolder) && !Directory.Exists(Path.GetDirectoryName(withoutImage.AudioPath)), "Deleting edited sounds removes their current generated assets and empty owned folders");
        check(File.ReadAllBytes(firstAudio).SequenceEqual(firstAudioBytes) && File.ReadAllBytes(secondAudio).SequenceEqual(secondAudioBytes) && File.ReadAllBytes(firstImage).SequenceEqual(firstImageBytes) && File.ReadAllBytes(secondImage).SequenceEqual(secondImageBytes) && File.ReadAllBytes(jpg).SequenceEqual(jpgBytes), "Every successful and failed edit preserves original MP3 and artwork source files");
    }

    private static void CheckFailedEdit(Action<bool, string> check, SoundboardStore store, SoundboardSound sound, Action edit, string description)
    {
        string catalog = Path.Combine(store.Root, "sounds.xml"); byte[] catalogBytes = File.ReadAllBytes(catalog), audioBytes = File.ReadAllBytes(sound.AudioPath), imageBytes = File.ReadAllBytes(sound.ImagePath);
        string[] files = OwnedFiles(sound); bool rejected = false;
        try { edit(); } catch (Exception) { rejected = true; }
        var restored = store.Load().Single(item => item.Id == sound.Id);
        check(rejected && restored.Name == sound.Name && restored.AudioPath == sound.AudioPath && restored.ImagePath == sound.ImagePath && File.ReadAllBytes(catalog).SequenceEqual(catalogBytes) && File.ReadAllBytes(sound.AudioPath).SequenceEqual(audioBytes) && File.ReadAllBytes(sound.ImagePath).SequenceEqual(imageBytes) && OwnedFiles(sound).SequenceEqual(files), description);
    }
    private static string[] OwnedFiles(SoundboardSound sound)
    { return Directory.GetFiles(Path.GetDirectoryName(sound.AudioPath)).OrderBy(path => path, StringComparer.Ordinal).ToArray(); }
    private static void CreateTone(string path, double frequency)
    {
        var tone = new OffsetSampleProvider(new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = frequency, Gain = 0.08 }) { Take = TimeSpan.FromMilliseconds(200) };
        MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(tone), path, 128000);
    }
    private static void CreateImage(string path, Color color, ImageFormat format)
    { using (var bitmap = new Bitmap(80, 80)) { using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color); bitmap.Save(path, format); } }
}
