using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Soundboard;

internal static class Program
{
    private static void Require(bool ok, string detail)
    {
        if (!ok) throw new InvalidOperationException(detail);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--drop"]) { DropCheck.Run(); return; }
        if (args is ["--formats", var corpus]) { DropCheck.Run(Path.GetFullPath(corpus)); return; }
        // Opt-in hardware bench. The standard checks use an isolated library.
        if (args is ["--live", var file, var route])
        {
            using var engine = new AudioEngine();
            var wave = Wave.Read(file);
            Console.WriteLine($"Route: {engine.Probe()}; A1: {engine.Devices[0]}; A2: {engine.Devices[1]}");
            var timer = Stopwatch.StartNew();
            engine.Play(wave, new(70, route == "a2" ? 1 : 0), route is "a1" or "a2");
            while (engine.Position >= 0 && timer.Elapsed < TimeSpan.FromSeconds(7)) Thread.Sleep(10);
            Require(engine.Position < 0, "No synchronized output callbacks from Potato.");
            Console.WriteLine($"Played {wave.Frames} source frames at {wave.Rate} Hz in {timer.ElapsedMilliseconds} ms.");
            return;
        }

        string temp = Directory.CreateTempSubdirectory("SoundboardChecks-").FullName;
        try
        {
            string assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds");
            var lib = new Library(temp, assets);
            Require(lib.State.Sounds.Count == 6, "First launch must include six playable starter sounds.");
            foreach (var sound in lib.State.Sounds)
            {
                var demo = Wave.Read(lib.FileFor(sound));
                Require(Math.Abs(1000.0 * demo.Frames / demo.Rate - sound.DurationMs) < 1, "Starter duration mismatch.");
            }
            Require(Library.Matches("Quick applause", "qapl") && !Library.Matches("Air horn", "coins"), "Search filter mismatch.");
            var attempts = new System.Collections.Generic.List<uint>();
            string? shortcut = Shell.BindLauncher(modifiers => { attempts.Add(modifiers); return modifiers == 6; });
            Require(shortcut == "Ctrl+Shift+Space" && attempts.SequenceEqual(new uint[] { 3, 6 }), "An occupied launcher shortcut must fall back to Ctrl+Shift+Space.");
            Require(Shell.BindLauncher(_ => true) == "Ctrl+Alt+Space" && Shell.BindLauncher(_ => false) == null, "The launcher must report the shortcut it actually acquired.");

            // A real 22.05 kHz mono file exercises the Windows decoder and stereo expansion.
            const int rate = 22050, frames = 3307;
            string mono = Path.Combine(temp, "Tést clip.wav");
            using (var writer = new BinaryWriter(File.Create(mono)))
            {
                writer.Write("RIFF"u8); writer.Write(36 + frames * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(frames * 2);
                for (int i = 0; i < frames; ++i) writer.Write((short)(i * 2));
            }
            var id = Guid.NewGuid();
            string pcm = Path.Combine(lib.ClipsDirectory, id.ToString("N") + ".wav");
            int ms = ClipImporter.DecodeAsync(mono, pcm).GetAwaiter().GetResult();
            Require(ms is >= 149 and <= 151, $"Decode failed ({ms} ms).");
            var decoded = Wave.Read(pcm);
            Require(decoded.Frames == frames && decoded.Rate == rate, "Decoder changed the duration or sample rate.");
            for (int i = 0; i < frames; ++i)
            {
                int left = BinaryPrimitives.ReadInt16LittleEndian(decoded.Bytes.AsSpan(i * 4));
                int right = BinaryPrimitives.ReadInt16LittleEndian(decoded.Bytes.AsSpan(i * 4 + 2));
                Require(left == i * 2 && right == left, "Mono import altered a sample.");
            }
            var added = new Sound(id, "Tést clip", ms, 4, "WAV");
            lib.Add(added);
            lib.Replace(added with { Name = "Renamed", Slot = 4, Favorite = true });
            lib.SetRouting(new(25, 1, true));
            // This is the catalog shape written by the previous release.
            File.WriteAllText(Path.Combine(temp, "library.json"), System.Text.Json.JsonSerializer.Serialize(new { lib.State.Sounds, lib.State.Routing }));
            lib = new Library(temp, assets);
            var restored = lib.State.Sounds.Single(s => s.Id == id);
            Require(restored is { Name: "Renamed", Slot: 4, Favorite: true } && lib.State.Routing == new Routing(25, 1, true), "Library settings failed to round-trip.");
            lib.Remove(restored);
            Require(!File.Exists(pcm) && File.Exists(mono) && new Library(temp, assets).State.Sounds.Count == 6, "Remove touched the wrong file.");
            string media = Path.Combine(AppContext.BaseDirectory, "Media");
            foreach (string name in new[] { "voice-note.opus", "tone.ogg", "tone.wv", "clip.webm", "program-stream.mpg" })
            {
                int duration = ClipImporter.DecodeAsync(Path.Combine(media, name), pcm).GetAwaiter().GetResult();
                var audio = Wave.Read(pcm);
                Require(duration is >= 150 and <= 450 && audio.Bytes.Any(b => b != 0), $"Bundled decoder failed on {name}.");
            }
            try
            {
                ClipImporter.DecodeAsync(Path.Combine(media, "silent-video.mp4"), pcm).GetAwaiter().GetResult();
                throw new Exception("A video without an audio track must be rejected.");
            }
            catch (InvalidDataException) { }
            lib.RememberFallbackHint();
            Require(new Library(temp, assets).State.FallbackHintSeen, "The successful fallback shortcut hint must be remembered.");
            var staged = added with { Id = Guid.NewGuid() };
            var missing = added with { Id = Guid.NewGuid() };
            File.Copy(mono, lib.StagingFile(staged.Id));
            try { lib.CommitImports([staged, missing]); throw new Exception("An incomplete batch was committed."); }
            catch (FileNotFoundException) { }
            Require(!File.Exists(Path.ChangeExtension(lib.StagingFile(staged.Id), null)) &&
                    new Library(temp, assets).State.Sounds.Count == 6, "A failed batch must roll back its files and catalog.");
            string invalid = Path.Combine(temp, "bad.mp3");
            File.WriteAllText(invalid, "not an audio file");
            try { ClipImporter.DecodeAsync(invalid, pcm).GetAwaiter().GetResult(); throw new Exception("Invalid media was accepted."); }
            catch (InvalidDataException) { }
            File.WriteAllText(Path.Combine(temp, "library.json"), "{");
            try { _ = new Library(temp, assets); throw new Exception("Damaged library was silently replaced."); }
            catch (System.Text.Json.JsonException) { }
            Console.WriteLine("Library passed: starter assets, fuzzy search, real codec, persistence, source preservation, invalid input.");
        }
        finally { Directory.Delete(temp, true); }
    }
}
