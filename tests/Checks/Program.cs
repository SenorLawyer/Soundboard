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
            int ms = 0;
            int hr = System.Threading.Tasks.Task.Run(() => NativeAudio.sb_decode(mono, pcm, out ms)).Result;
            Require(hr == 0 && ms is >= 149 and <= 151, $"Decode failed (0x{hr:X8}, {ms} ms).");
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
            lib = new Library(temp, assets);
            var restored = lib.State.Sounds.Single(s => s.Id == id);
            Require(restored is { Name: "Renamed", Slot: 4, Favorite: true } && lib.State.Routing == new Routing(25, 1, true), "Library settings failed to round-trip.");
            lib.Remove(restored);
            Require(!File.Exists(pcm) && File.Exists(mono) && new Library(temp, assets).State.Sounds.Count == 6, "Remove touched the wrong file.");
            string invalid = Path.Combine(temp, "bad.mp3");
            File.WriteAllText(invalid, "not an audio file");
            Require(System.Threading.Tasks.Task.Run(() => NativeAudio.sb_decode(invalid, pcm, out _)).Result < 0, "Invalid media was accepted.");
            File.WriteAllText(Path.Combine(temp, "library.json"), "{");
            try { _ = new Library(temp, assets); throw new Exception("Damaged library was silently replaced."); }
            catch (System.Text.Json.JsonException) { }
            Console.WriteLine("Library passed: starter assets, fuzzy search, real codec, persistence, source preservation, invalid input.");
        }
        finally { Directory.Delete(temp, true); }
    }
}
