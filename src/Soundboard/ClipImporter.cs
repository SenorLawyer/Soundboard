using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Soundboard;

internal static class ClipImporter
{
    private const int Rate = 48000;
    private const int BytesPerFrame = 4;
    private const int MaxFrames = Rate * 300;

    public static async Task<int> DecodeAsync(string source, string destination)
    {
        var file = new FileInfo(source);
        if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("Choose a source smaller than 256 MB.");
        bool nativeFormat = file.Extension.ToLowerInvariant() is ".wav" or ".mp3" or ".m4a" or ".aac" or ".mp4" or ".wma" or ".flac";
        if (nativeFormat && File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll")) &&
            File.Exists(Path.Combine(Environment.SystemDirectory, "mfreadwrite.dll")))
        {
            var decoded = await Task.Run(() =>
            {
                int hr = NativeAudio.sb_decode(file.FullName, destination, out int duration);
                return (hr, duration);
            }).ConfigureAwait(false);
            if (decoded.hr >= 0) return decoded.duration;
        }
        return await DecodeWithFfmpeg(file.FullName, destination).ConfigureAwait(false);
    }

    private static async Task<int> DecodeWithFfmpeg(string source, string destination)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "Codecs", "ffmpeg.exe");
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // Decode just past the limit so a long source is rejected instead of imported as a partial clip.
        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-threads", "1",
            "-protocol_whitelist", "file,pipe", "-i", source, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-t", "300.1", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Win32Exception ex) { throw new IOException("Could not open the audio decoder. Extract the complete Windows archive.", ex); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            file.Position = 44;
            var buffer = new byte[65536];
            int length = 0, read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
            {
                length += read;
                if (length > MaxFrames * BytesPerFrame) throw new InvalidDataException("Clips must fit within five minutes.");
                await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || length <= 0 || length % BytesPerFrame != 0)
                throw new InvalidDataException("This file has no readable audio track.");

            file.Position = 0;
            using var header = new BinaryWriter(file);
            header.Write("RIFF"u8); header.Write(36 + length); header.Write("WAVEfmt "u8); header.Write(16);
            header.Write((short)1); header.Write((short)2); header.Write(Rate); header.Write(Rate * BytesPerFrame);
            header.Write((short)BytesPerFrame); header.Write((short)16); header.Write("data"u8); header.Write(length);
            return (int)((length / BytesPerFrame * 1000L + Rate - 1) / Rate);
        }
        catch (OperationCanceledException) { throw new InvalidDataException("The decoder timed out reading this file."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }
}
