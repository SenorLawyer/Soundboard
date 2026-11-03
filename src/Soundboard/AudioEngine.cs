using Microsoft.Win32;
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Soundboard;

internal static class NativeAudio
{
    internal const string Dll = "Soundboard.Audio.dll";
    [DllImport(Dll)] internal static extern void sb_set_clip(IntPtr samples, int frames, int rate, int b1, int monitor, float gain);
    [DllImport(Dll)] internal static extern void sb_clear();
    [DllImport(Dll)] internal static extern int sb_position();
    [DllImport(Dll)] internal static extern void sb_gain(float value);
}

internal sealed record Wave(byte[] Bytes, int Rate, int Frames)
{
    // Files in the library always use our canonical stereo PCM header.
    internal static Wave Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is <= 44 or > (128L * 1024 * 1024 + 44)) throw new InvalidDataException("This clip is empty or too large.");
        Span<byte> h = stackalloc byte[44];
        stream.ReadExactly(h);
        static int Word(ReadOnlySpan<byte> header, int offset) => BinaryPrimitives.ReadInt32LittleEndian(header[offset..]);
        int rate = Word(h, 24), size = Word(h, 40);
        if (!h[..4].SequenceEqual("RIFF"u8) || !h[8..16].SequenceEqual("WAVEfmt "u8) || !h[36..40].SequenceEqual("data"u8) ||
            Word(h, 16) != 16 || Word(h, 20) != 0x00020001 || Word(h, 32) != 0x00100004 ||
            rate is < 8000 or > 192000 || size <= 0 || size % 4 != 0 || size != stream.Length - 44 || size / 4 > (long)rate * 300)
            throw new InvalidDataException("This library clip is damaged. Import the original file again.");
        var pcm = GC.AllocateUninitializedArray<byte>(size, pinned: true);
        stream.ReadExactly(pcm);
        return new(pcm, rate, size / 4);
    }
}

internal sealed class AudioEngine : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Call();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Query(out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Register(int mode, IntPtr callback, IntPtr user, [In, Out] byte[] name);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)] private delegate int GetName(string parameter, IntPtr result);
    private IntPtr vendor, native;
    private Call login = null!, logout = null!, dirty = null!, start = null!, stop = null!, unregister = null!;
    private Query kind = null!;
    private Register register = null!;
    private GetName getName = null!;
    private bool loggedIn, registered;
    private Wave? current; // Roots the pinned samples until sb_clear has left the render lock.
    public string[] Devices { get; } = ["Hardware output A1", "Hardware output A2"];
    public bool Ready { get; private set; }
    public int Position => current == null ? -1 : NativeAudio.sb_position();

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(vendor, "VBVMR_" + name));

    private static string? VendorPath()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}");
            if (key?.GetValue("UninstallString") is string raw)
            {
                string exe = raw.StartsWith('"') ? raw.Split('"').ElementAtOrDefault(1) ?? "" : raw.Trim();
                if (Path.IsPathFullyQualified(exe) && Path.GetDirectoryName(exe) is { } folder)
                {
                    var candidate = Path.Combine(folder, "VoicemeeterRemote64.dll");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VB", "Voicemeeter", "VoicemeeterRemote64.dll");
        return File.Exists(fallback) ? fallback : null;
    }

    public string Probe()
    {
        Ready = false;
        if (vendor == IntPtr.Zero)
        {
            var path = VendorPath();
            if (path == null) return "Install Voicemeeter Potato to play sounds.";
            vendor = NativeLibrary.Load(path);
            try
            {
                login = Export<Call>("Login"); logout = Export<Call>("Logout");
                dirty = Export<Call>("IsParametersDirty"); kind = Export<Query>("GetVoicemeeterType");
                register = Export<Register>("AudioCallbackRegister"); start = Export<Call>("AudioCallbackStart");
                stop = Export<Call>("AudioCallbackStop"); unregister = Export<Call>("AudioCallbackUnregister");
                getName = Export<GetName>("GetParameterStringW");
                native = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, NativeAudio.Dll));
            }
            catch { NativeLibrary.Free(vendor); vendor = IntPtr.Zero; throw; }
        }
        if (!loggedIn)
        {
            int rc = login();
            loggedIn = rc >= 0;
            if (!loggedIn) return "Voicemeeter could not open a Remote API connection.";
        }
        if (dirty() < 0 || kind(out var type) != 0) return "Open Voicemeeter Potato to connect.";
        if (type != 3) return "Select Voicemeeter Potato for B1 playback.";
        Ready = true;
        var buf = Marshal.AllocHGlobal(1024);
        try
        {
            for (int i = 0; i < 2; ++i)
            {
                Marshal.WriteInt16(buf, 0);
                var name = getName($"Bus[{i}].Device.name", buf) == 0 ? Marshal.PtrToStringUni(buf) : null;
                Devices[i] = string.IsNullOrWhiteSpace(name) ? $"Hardware output A{i + 1}" : name;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return "Voicemeeter Out B1";
    }

    public void Play(Wave wave, Routing routing, bool preview)
    {
        string status = Probe();
        if (!Ready) throw new InvalidOperationException(status);
        bool fresh = !registered;
        if (fresh)
        {
            var name = new byte[64];
            Encoding.ASCII.GetBytes("Soundboard", name);
            int rc = register(2, NativeLibrary.GetExport(native, "sb_callback"), IntPtr.Zero, name);
            if (rc == 1) throw new InvalidOperationException($"Output insert in use by {Encoding.ASCII.GetString(name).TrimEnd('\0')}. Close its insert to play.");
            if (rc != 0) throw new InvalidOperationException($"Voicemeeter refused the output insert ({rc}).");
            registered = true;
        }
        NativeAudio.sb_clear();
        current = wave;
        NativeAudio.sb_set_clip(Marshal.UnsafeAddrOfPinnedArrayElement(wave.Bytes, 0), wave.Frames, wave.Rate,
            preview ? 0 : 1, preview || routing.Monitor ? routing.PreviewBus : -1, routing.Volume / 100f);
        if (fresh && start() != 0)
        {
            Stop();
            throw new InvalidOperationException("Voicemeeter could not start its output insert.");
        }
    }

    public void Stop()
    {
        if (current != null) NativeAudio.sb_clear();
        current = null;
        if (registered) { stop(); unregister(); registered = false; }
    }

    public void Dispose()
    {
        Stop();
        if (loggedIn) { logout(); loggedIn = false; }
        if (vendor != IntPtr.Zero) { NativeLibrary.Free(vendor); vendor = IntPtr.Zero; }
        if (native != IntPtr.Zero) { NativeLibrary.Free(native); native = IntPtr.Zero; }
    }
}
