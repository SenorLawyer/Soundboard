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

    public static async Task<int> DecodeAsync(string source, string destination, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var file = new FileInfo(source);
        long bytes = await Task.Run(() => file.Length, cancellation).WaitAsync(cancellation).ConfigureAwait(false);
        if (bytes > 256L * 1024 * 1024) throw new InvalidDataException("Choose a source smaller than 256 MiB.");
        bool nativeFormat = file.Extension.ToLowerInvariant() is ".wav" or ".mp3" or ".m4a" or ".aac" or ".mp4" or ".wma" or ".flac";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            if (nativeFormat && File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll")) &&
                File.Exists(Path.Combine(Environment.SystemDirectory, "mfreadwrite.dll")))
            {
                var native = Helper("Soundboard.Media.exe");
                native.ArgumentList.Add(file.FullName); native.ArgumentList.Add(destination);
                using var worker = Start(native);
                try
                {
                    await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    if (worker.ExitCode is > 0 and <= 300000) return worker.ExitCode;
                }
                finally { await Finish(worker).ConfigureAwait(false); }
            }
            return await DecodeWithFfmpeg(file.FullName, destination, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidDataException("The decoder timed out reading this file.");
        }
    }

    private static ProcessStartInfo Helper(string name) => new(Path.Combine(AppContext.BaseDirectory, "Codecs", name))
        { UseShellExecute = false, CreateNoWindow = true };

    private static Process Start(ProcessStartInfo info)
    {
        try { return Process.Start(info) ?? throw new IOException("Could not start the audio decoder."); }
        catch (Win32Exception ex) { throw new IOException("Could not open the audio decoder. Extract the complete Windows archive.", ex); }
    }

    private static async Task Finish(Process process)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
    }

    private static async Task<int> DecodeWithFfmpeg(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = Helper("ffmpeg.exe");
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        // Decode just past the limit so a long source is rejected instead of imported as a partial clip.
        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-v", "error", "-xerror", "-threads", "1",
            "-protocol_whitelist", "file,pipe", "-i", source, "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-t", "300.1", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(arg);

        using var process = Start(start);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            file.Position = 44;
            var buffer = new byte[65536];
            int length = 0, read;
            while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                length += read;
                if (length > MaxFrames * BytesPerFrame) throw new InvalidDataException("Clips must fit within five minutes.");
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0 || length <= 0 || length % BytesPerFrame != 0)
                throw new InvalidDataException("This file has no readable audio track.");

            file.Position = 0;
            using var header = new BinaryWriter(file);
            header.Write("RIFF"u8); header.Write(36 + length); header.Write("WAVEfmt "u8); header.Write(16);
            header.Write((short)1); header.Write((short)2); header.Write(Rate); header.Write(Rate * BytesPerFrame);
            header.Write((short)BytesPerFrame); header.Write((short)16); header.Write("data"u8); header.Write(length);
            return (int)((length / BytesPerFrame * 1000L + Rate - 1) / Rate);
        }
        finally
        {
            await Finish(process).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }
}
