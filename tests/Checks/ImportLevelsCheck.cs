using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Soundboard;

internal static class ImportLevelsCheck
{
    internal static void WriteQuietVoice(string assets, string destination)
    {
        byte[] wave = File.ReadAllBytes(Path.Combine(assets, new Guid(6, 0, 0, new byte[8]).ToString("N") + ".wav"));
        var samples = MemoryMarshal.Cast<byte, short>(wave.AsSpan(44));
        double gain = Math.Pow(10, -24.0 / 20);
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)Math.Round(samples[i] * gain);
        File.WriteAllBytes(destination, wave);
    }

    internal static void Export(string folder, string source, string imported)
    {
        Directory.CreateDirectory(folder);
        File.Copy(source, Path.Combine(folder, "quiet-source.wav"), true);
        File.Copy(imported, Path.Combine(folder, "quiet-import.wav"), true);
    }

    internal static async Task Run(string temp, string assets, string? proof = null)
    {
        var library = new Library(Path.Combine(temp, "level-library"), assets);
        string source = Path.Combine(temp, "quiet.wav");
        WriteQuietVoice(assets, source);
        byte[] original = File.ReadAllBytes(source);
        var id = Guid.NewGuid();
        int duration = await ClipImporter.ImportAsync(source, library.StagingFile(id), CancellationToken.None);
        var pad = new Sound(id, "Quiet count", duration, 0, "WAV");
        library.CommitImports([pad]);
        var imported = await library.ReadForPlaybackAsync(pad, CancellationToken.None);
        static int Peak(Wave wave)
        {
            int peak = 0;
            foreach (short sample in MemoryMarshal.Cast<byte, short>(wave.Bytes))
                peak = Math.Max(peak, Math.Abs((int)sample));
            return peak;
        }
        var quiet = Wave.Read(source);
        if (imported.Frames != quiet.Frames || imported.Rate != quiet.Rate || Peak(imported) < 8 * Peak(quiet) ||
            !File.ReadAllBytes(source).SequenceEqual(original))
            throw new Exception("Quiet import changed the source, lost frames or stayed quiet.");
        if (proof != null) Export(proof, source, library.FileFor(pad));

        var legacy = pad with { Id = Guid.NewGuid(), Name = "Saved name", Slot = 3, Favorite = true };
        string oldFile = Path.Combine(library.ClipsDirectory, legacy.Id.ToString("N") + ".wav");
        File.WriteAllBytes(oldFile, original);
        library.Add(legacy);
        var first = await library.ReadForPlaybackAsync(legacy, CancellationToken.None);
        string cached = library.FileFor(legacy);
        if (cached == oldFile || !first.Bytes.SequenceEqual(imported.Bytes) || !File.ReadAllBytes(oldFile).SequenceEqual(original))
            throw new Exception("An existing pad must get the same level as a fresh import while retaining its original.");
        var reopened = new Library(Path.Combine(temp, "level-library"), assets);
        if (reopened.FileFor(legacy) != cached || reopened.State.Sounds.Single(s => s.Id == legacy.Id) != legacy)
            throw new Exception("An existing pad lost its cache or catalog settings on restart.");
        reopened.Remove(legacy);
        if (File.Exists(oldFile) || File.Exists(cached)) throw new Exception("Removing a pad left an audio copy behind.");

        reopened.Add(legacy);
        File.WriteAllBytes(oldFile, original);
        using (var cancel = new CancellationTokenSource())
        {
            var preparation = reopened.ReadForPlaybackAsync(legacy, cancel.Token);
            cancel.Cancel();
            reopened.Remove(legacy);
            try { await preparation; throw new Exception("Cancelled pad preparation completed."); }
            catch (OperationCanceledException) { }
        }
        if (File.Exists(cached) || Directory.EnumerateFiles(reopened.ClipsDirectory, "*.part").Any())
            throw new Exception("Cancelled preparation left a cache or a staged file.");

        static void WriteStereo(string path, int sampleRate, int frames, Func<int, short> sample, bool opposite = false)
        {
            using var file = new BinaryWriter(File.Create(path));
            file.Write("RIFF"u8); file.Write(36 + frames * 4); file.Write("WAVEfmt "u8); file.Write(16);
            file.Write((short)1); file.Write((short)2); file.Write(sampleRate); file.Write(sampleRate * 4);
            file.Write((short)4); file.Write((short)16); file.Write("data"u8); file.Write(frames * 4);
            for (int i = 0; i < frames; i++)
            {
                short left = sample(i);
                file.Write(left); file.Write((short)(opposite ? -left + 1 : 0));
            }
        }
        string result = Path.Combine(temp, "leveled.wav");
        WriteStereo(source, 8000, 1, _ => 0);
        await ClipImporter.LevelAsync(source, result, default);
        var silent = Wave.Read(result);
        if (silent.Frames != 1 || silent.Rate != 8000 || Peak(silent) != 0)
            throw new Exception("A silent one-frame clip must stay silent and keep its duration.");
        WriteStereo(source, 48000, 48000, i => (short)Math.Round(4 * Math.Sin(i * 0.08)));
        await ClipImporter.LevelAsync(source, result, default);
        if (Peak(Wave.Read(result)) is < 80 or > 155) throw new Exception("Very quiet audio needs a bounded boost.");
        WriteStereo(source, 96000, 4800, i => (short)(i == 1200 ? 32000 : 650 * Math.Sin(i * 0.08)));
        await ClipImporter.LevelAsync(source, result, default);
        var shot = Wave.Read(result);
        if (shot.Frames != 4800 || shot.Rate != 96000 || Peak(shot) > 30000 ||
            MemoryMarshal.Cast<byte, short>(shot.Bytes).ToArray().Where((_, i) => i % 2 == 1).Any(s => s != 0))
            throw new Exception("A short panned clip lost its timing, stereo placement or peak headroom.");
        WriteStereo(source, 48000, 48000, i => (short)(30000 * Math.Sin(i * 0.08)));
        await ClipImporter.LevelAsync(source, result, default);
        if (Peak(Wave.Read(result)) >= 27000) throw new Exception("A loud clip was not turned down.");
        WriteStereo(source, 48000, 48000, i => (short)(15000 * Math.Sin(i * 0.08)), opposite: true);
        await ClipImporter.LevelAsync(source, result, default);
        if (Peak(Wave.Read(result)) >= 24000) throw new Exception("Stereo cancellation caused excessive gain.");
        Console.WriteLine("Import leveling passed: quiet voice, loud audio, short stereo peak, silence and existing library.");
    }
}
