using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Soundboard;

public sealed record Sound(Guid Id, string Name, int DurationMs, int Icon, string Format, bool Favorite = false, int Slot = 0)
{
    public string DurationLabel => DurationMs < 10000 ? $"{DurationMs / 1000.0:0.00} s" : TimeSpan.FromMilliseconds(DurationMs).ToString(@"m\:ss");
    public string Shortcut => Slot > 0 ? $"Ctrl Alt {Slot}" : "";
}

public sealed record Routing(int Volume = 70, int PreviewBus = 0, bool Monitor = false);
public sealed record Catalog(List<Sound> Sounds, Routing Routing, bool FallbackHintSeen = false);

public sealed class Library
{
    public static readonly string[] Extensions = [
        ".wav", ".wave", ".w64", ".mp3", ".mp2", ".m4a", ".m4b", ".aac", ".adts", ".flac", ".ogg", ".oga", ".opus",
        ".wma", ".aif", ".aiff", ".aifc", ".caf", ".au", ".snd", ".wv", ".ape", ".ac3", ".eac3", ".dts", ".amr", ".tta",
        ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".mka", ".avi", ".wmv", ".asf", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts",
        ".3gp", ".3g2", ".mts", ".vob", ".ogv", ".m4r"
    ];
    private static readonly HashSet<string> AllowedExtensions = new(Extensions, StringComparer.OrdinalIgnoreCase);
    public static bool Supports(string path) => AllowedExtensions.Contains(Path.GetExtension(path));
    private readonly string root;
    private readonly string assets;
    private readonly string index;
    private readonly object clipFiles = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    public Catalog State { get; private set; }
    public string ClipsDirectory => Path.Combine(root, "Clips");

    public Library(string root, string assets)
    {
        this.root = root;
        this.assets = assets;
        Directory.CreateDirectory(ClipsDirectory);
        index = Path.Combine(root, "library.json");
        State = File.Exists(index) ? Read(index) : new Catalog([.. Starters], new Routing());
    }

    private Catalog Read(string path)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), Json);
        if (catalog?.Sounds == null || catalog.Routing is not { Volume: >= 0 and <= 100, PreviewBus: 0 or 1 } ||
            catalog.Sounds.Any(s => s == null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name) ||
                                    s.Format == null || s.DurationMs <= 0 || s.Icon < 0 || s.Slot is < 0 or > 9) ||
            catalog.Sounds.Select(s => s.Id).Distinct().Count() != catalog.Sounds.Count ||
            catalog.Sounds.Where(s => s.Slot != 0).GroupBy(s => s.Slot).Any(g => g.Count() > 1))
            throw new InvalidDataException("The sound library is damaged. Your clip files are still in the Clips folder.");
        // A saved starter uses the recording shipped with this build unless it has a local copy.
        for (int i = 0; i < catalog.Sounds.Count; i++)
        {
            var sound = catalog.Sounds[i];
            if (StarterDurations.TryGetValue(sound.Id, out int duration) && sound.DurationMs != duration &&
                !File.Exists(LocalFile(sound.Id)) && !File.Exists(LeveledFile(sound.Id)))
                catalog.Sounds[i] = sound with { DurationMs = duration };
        }
        return catalog;
    }

    private static readonly Sound[] Starters = [
        Starter(1, "Air horn", 1580, 0, true),
        Starter(2, "Quick applause", 2750, 1, true),
        Starter(3, "Coin drop", 1290, 2),
        Starter(4, "Glass ping", 1220, 3),
        Starter(5, "Power down", 1080, 4),
        Starter(6, "Signal check", 1914, 5)
    ];
    private static readonly Dictionary<Guid, int> StarterDurations = Starters.ToDictionary(s => s.Id, s => s.DurationMs);

    private static Sound Starter(int n, string name, int ms, int icon, bool favorite = false) =>
        new(new Guid(n, 0, 0, new byte[8]), name, ms, icon, "WAV", favorite);

    private string LocalFile(Guid id) => Path.Combine(ClipsDirectory, id.ToString("N") + ".wav");
    private string LeveledFile(Guid id) => Path.Combine(ClipsDirectory, id.ToString("N") + ".leveled.wav");

    public string FileFor(Sound sound)
    {
        string leveled = LeveledFile(sound.Id);
        if (File.Exists(leveled)) return leveled;
        string legacy = LocalFile(sound.Id);
        return File.Exists(legacy) ? legacy : Path.Combine(assets, Path.GetFileName(legacy));
    }

    internal async Task<Wave> ReadForPlaybackAsync(Sound sound, CancellationToken token)
    {
        string path = FileFor(sound);
        if (path == LocalFile(sound.Id))
        {
            // Older libraries only have the decoded original. Publish its leveled copy once.
            string leveled = LeveledFile(sound.Id);
            string workId = Guid.NewGuid().ToString("N");
            string snapshot = Path.Combine(Path.GetTempPath(), "Soundboard-" + workId + ".wav");
            string temp = leveled + "." + workId + ".part";
            bool cache = true;
            try
            {
                try { using var reserved = new FileStream(temp, FileMode.CreateNew, FileAccess.Write); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // Read-only libraries can play the result from temporary storage.
                    temp = snapshot + ".part"; cache = false;
                }
                // The helper can keep its input open. A private snapshot lets the user remove
                // the pad immediately even while an old recording is being measured.
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true))
                using (var copy = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(copy, token).ConfigureAwait(false);
                await ClipImporter.LevelAsync(snapshot, temp, token).ConfigureAwait(false);
                if (!cache) return await Task.Run(() => Wave.Read(temp), token).ConfigureAwait(false);
                lock (clipFiles)
                {
                    token.ThrowIfCancellationRequested();
                    if (!File.Exists(path)) throw new FileNotFoundException("This sound was removed.");
                    if (!File.Exists(leveled)) File.Move(temp, leveled);
                }
            }
            finally { File.Delete(temp); File.Delete(snapshot); }
            path = leveled;
        }
        return await Task.Run(() => Wave.Read(path), token).ConfigureAwait(false);
    }

    private void Save(Catalog next)
    {
        var temp = index + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, next, Json);
            file.Flush(true);
        }
        File.Move(temp, index, true);
        State = next;
    }

    public void Add(Sound sound) => Save(State with { Sounds = [.. State.Sounds, sound] });
    internal string StagingFile(Guid id) => LeveledFile(id) + ".part";

    internal void CommitImports(IReadOnlyList<Sound> sounds)
    {
        var promoted = new List<string>(sounds.Count);
        try
        {
            foreach (var sound in sounds)
            {
                var staged = StagingFile(sound.Id);
                var wave = Path.ChangeExtension(staged, null);
                File.Move(staged, wave);
                promoted.Add(wave);
            }
            Save(State with { Sounds = [.. State.Sounds, .. sounds] });
        }
        catch
        {
            foreach (string file in promoted) File.Delete(file);
            throw;
        }
    }
    public void Replace(Sound sound) => Save(State with { Sounds = State.Sounds.Select(s => s.Id == sound.Id ? sound : s).ToList() });
    public void SetRouting(Routing routing) => Save(State with { Routing = routing });
    public void RememberFallbackHint() => Save(State with { FallbackHintSeen = true });
    public void Remove(Sound sound)
    {
        Save(State with { Sounds = State.Sounds.Where(s => s.Id != sound.Id).ToList() });
        lock (clipFiles)
        {
            File.Delete(LocalFile(sound.Id));
            File.Delete(LeveledFile(sound.Id));
        }
    }

    public static bool Matches(string value, string query)
    {
        if (query.Length == 0) return true;
        int i = 0;
        foreach (char c in value)
        {
            if (char.ToUpperInvariant(c) != char.ToUpperInvariant(query[i])) continue;
            if (++i == query.Length) return true;
        }
        return false;
    }
}
