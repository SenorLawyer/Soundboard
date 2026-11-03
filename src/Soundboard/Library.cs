using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Soundboard;

public sealed record Sound(Guid Id, string Name, int DurationMs, int Icon, string Format, bool Favorite = false, int Slot = 0)
{
    public string DurationLabel => DurationMs < 10000 ? $"{DurationMs / 1000.0:0.00} s" : TimeSpan.FromMilliseconds(DurationMs).ToString(@"m\:ss");
    public string Section => Favorite ? "Favorites" : "Library";
    public string Shortcut => Slot > 0 ? $"Ctrl Alt {Slot}" : "";
}

public sealed record Routing(int Volume = 70, int PreviewBus = 0, bool Monitor = false);
public sealed record Catalog(List<Sound> Sounds, Routing Routing);

public sealed class Library
{
    public static readonly string[] Extensions = [".wav", ".mp3", ".m4a", ".mp4", ".aac", ".flac", ".wma", ".aif", ".aiff", ".ogg"];
    private readonly string root;
    private readonly string assets;
    private readonly string index;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    public Catalog State { get; private set; }
    public string ClipsDirectory => Path.Combine(root, "Clips");

    public Library(string root, string assets)
    {
        this.root = root;
        this.assets = assets;
        Directory.CreateDirectory(ClipsDirectory);
        index = Path.Combine(root, "library.json");
        State = File.Exists(index) ? Read(index) : Defaults();
    }

    private static Catalog Read(string path)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), Json);
        if (catalog?.Sounds == null || catalog.Routing is not { Volume: >= 0 and <= 100, PreviewBus: 0 or 1 } ||
            catalog.Sounds.Any(s => s == null || s.Id == Guid.Empty || string.IsNullOrWhiteSpace(s.Name) ||
                                    s.Format == null || s.DurationMs <= 0 || s.Icon < 0 || s.Slot is < 0 or > 9) ||
            catalog.Sounds.Select(s => s.Id).Distinct().Count() != catalog.Sounds.Count ||
            catalog.Sounds.Where(s => s.Slot != 0).GroupBy(s => s.Slot).Any(g => g.Count() > 1))
            throw new InvalidDataException("The sound library is damaged. Your clip files are still in the Clips folder.");
        return catalog;
    }

    private static Catalog Defaults() => new([
        Starter(1, "Air horn", 1150, 0, true),
        Starter(2, "Quick applause", 1600, 1, true),
        Starter(3, "Coin drop", 690, 2),
        Starter(4, "Glass ping", 920, 3),
        Starter(5, "Power down", 1080, 4),
        Starter(6, "Signal check", 780, 5)
    ], new Routing());

    private static Sound Starter(int n, string name, int ms, int icon, bool favorite = false) =>
        new(new Guid(n, 0, 0, new byte[8]), name, ms, icon, "WAV", favorite);

    public string FileFor(Sound sound)
    {
        var name = sound.Id.ToString("N") + ".wav";
        var path = Path.Combine(ClipsDirectory, name);
        return File.Exists(path) ? path : Path.Combine(assets, name);
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
    public void Replace(Sound sound) => Save(State with { Sounds = State.Sounds.Select(s => s.Id == sound.Id ? sound : s).ToList() });
    public void SetRouting(Routing routing) => Save(State with { Routing = routing });
    public void Remove(Sound sound)
    {
        Save(State with { Sounds = State.Sounds.Where(s => s.Id != sound.Id).ToList() });
        File.Delete(Path.Combine(ClipsDirectory, sound.Id.ToString("N") + ".wav"));
    }

    public static bool Matches(string value, string query)
    {
        int i = 0;
        foreach (char c in value)
            if (i < query.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[i])) i++;
        return i == query.Length;
    }
}
