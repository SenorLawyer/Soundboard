using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soundboard;

internal static class ClipImporter
{
    private const int Rate = 48000;
    private const int BytesPerFrame = 4;
    internal static async Task<int> ImportAsync(string source, string destination, CancellationToken token)
    {
        string decoded = destination + ".decode";
        try
        {
            int duration = await DecodeAsync(source, decoded, token).ConfigureAwait(false);
            await LevelAsync(decoded, destination, token).ConfigureAwait(false);
            return duration;
        }
        finally { File.Delete(decoded); }
    }

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

    private static ProcessStartInfo Ffmpeg(string source, string verbosity)
    {
        var start = Helper("ffmpeg.exe");
        start.RedirectStandardError = true;
        foreach (string arg in new[] { "-nostdin", "-hide_banner", "-nostats", "-v", verbosity, "-xerror", "-threads", "1",
            "-filter_threads", "1", "-protocol_whitelist", "file,pipe", "-i", source, "-map", "0:a:0", "-vn", "-sn", "-dn" })
            start.ArgumentList.Add(arg);
        return start;
    }

    private static Task<int> DecodeWithFfmpeg(string source, string destination, CancellationToken token)
    {
        var start = Ffmpeg(source, "error");
        // Decode just past the limit so a long source is rejected instead of imported as a partial clip.
        start.ArgumentList.Add("-t"); start.ArgumentList.Add("300.1");
        return WriteWaveAsync(start, destination, Rate, null, token);
    }

    internal static async Task LevelAsync(string source, string destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        int rate, size;
        using (var file = File.OpenRead(source)) (rate, size) = Wave.ReadHeader(file);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var gain = await MeasureGainAsync(source, timeout.Token).ConfigureAwait(false);
            var start = Ffmpeg(source, "error");
            // Look ahead at 192 kHz for stereo peaks. Padding flushes the limiter and resamplers
            // even on one-frame clips. The trim restores the exact original frame count.
            string filter = FormattableString.Invariant($"apad=pad_dur=0.1,volume={gain:F2}dB:precision=double,aresample=192000,alimiter=limit=0.75:attack=5:release=60:level=0:latency=1,aresample={rate},atrim=end_sample={size / BytesPerFrame}");
            start.ArgumentList.Add("-af"); start.ArgumentList.Add(filter);
            await WriteWaveAsync(start, destination, rate, size, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new InvalidDataException("Leveling this clip timed out.");
        }
    }

    private static async Task<double> MeasureGainAsync(string source, CancellationToken token)
    {
        var start = Ffmpeg(source, "info");
        // The second meter hears the mono voice feed. The first guards against overboosting
        // stereo whose channels cancel in mono. Padding includes the last partial 400 ms window.
        const string meters = "apad=pad_dur=0.4,ebur128@stereo=framelog=verbose,pan=mono|c0=0.5*c0+0.5*c1,ebur128@voice=framelog=verbose";
        foreach (string arg in new[] { "-af", meters, "-f", "null", "-" })
            start.ArgumentList.Add(arg);
        token.ThrowIfCancellationRequested();
        using var process = Start(start);
        var report = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string summary = await report.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidDataException("Could not measure this clip's volume.");
            double Integrated(string meter)
            {
                var match = Regex.Match(summary, @"\[ebur128@" + meter + @" @ [^\]]+\] Summary:\s*Integrated loudness:\s*I:\s*([-+]?\d+(?:\.\d+)?) LUFS");
                return double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double value)
                    ? value : throw new InvalidDataException("Could not read the clip's loudness measurement.");
            }
            double loudness = Math.Max(Integrated("voice"), Integrated("stereo") - 6);
            // The global 70% send turns a -15 LUFS library copy into roughly -18 LUFS on B1.
            return Math.Min(30, -15 - loudness);
        }
        finally { await Finish(process).ConfigureAwait(false); await report.ConfigureAwait(false); }
    }

    private static async Task<int> WriteWaveAsync(ProcessStartInfo start, string destination, int rate, int? expectedSize, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        start.RedirectStandardOutput = true;
        foreach (string arg in new[] { "-ac", "2", "-ar", rate.ToString(CultureInfo.InvariantCulture), "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1" })
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
                if (length > (long)rate * 300 * BytesPerFrame) throw new InvalidDataException("Clips must fit within five minutes.");
                if (expectedSize.HasValue && length > expectedSize) throw new InvalidDataException("Leveling changed the clip length.");
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0 || length <= 0 || length % BytesPerFrame != 0)
                throw new InvalidDataException("This file has no readable audio track.");
            if (expectedSize.HasValue && length != expectedSize) throw new InvalidDataException("Leveling changed the clip length.");

            file.Position = 0;
            using var header = new BinaryWriter(file);
            header.Write("RIFF"u8); header.Write(36 + length); header.Write("WAVEfmt "u8); header.Write(16);
            header.Write((short)1); header.Write((short)2); header.Write(rate); header.Write(rate * BytesPerFrame);
            header.Write((short)BytesPerFrame); header.Write((short)16); header.Write("data"u8); header.Write(length);
            return (int)((length / BytesPerFrame * 1000L + rate - 1) / rate);
        }
        finally
        {
            await Finish(process).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
    }
}
