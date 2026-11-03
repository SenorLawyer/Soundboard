"""Generate a local corpus for 'dotnet run --project tests/Checks -- --formats <dir>'."""
import argparse
import hashlib
import subprocess
import urllib.request
from pathlib import Path

# Extension, container, audio codec, optional video codec.
FORMATS = [
    ("wav", "wav", "pcm_s16le"), ("wave", "wav", "pcm_s16le"), ("w64", "w64", "pcm_s16le"),
    ("mp3", "mp3", "libmp3lame"), ("mp2", "mp2", "mp2"),
    ("m4a", "ipod", "alac"), ("m4b", "ipod", "aac"), ("m4r", "ipod", "aac"),
    ("aac", "adts", "aac"), ("adts", "adts", "aac"), ("flac", "flac", "flac"),
    ("ogg", "ogg", "libvorbis"), ("oga", "ogg", "libvorbis"), ("opus", "opus", "libopus"),
    ("wma", "asf", "wmav2"), ("aif", "aiff", "pcm_s16be"), ("aiff", "aiff", "pcm_s16be"),
    ("aifc", "aiff", "pcm_s16le"), ("caf", "caf", "pcm_s16le"), ("au", "au", "pcm_mulaw"),
    ("snd", "au", "pcm_mulaw"), ("wv", "wv", "wavpack"), ("ac3", "ac3", "ac3"),
    ("eac3", "eac3", "eac3"), ("dts", "dts", "dca"), ("amr", "amr", "libopencore_amrnb"),
    ("tta", "tta", "tta"), ("mp4", "mp4", "aac", "mpeg4"), ("m4v", "mp4", "aac", "mpeg4"),
    ("mov", "mov", "aac", "mpeg4"), ("webm", "webm", "libopus", "libvpx"),
    ("mkv", "matroska", "libvorbis", "mpeg4"), ("mka", "matroska", "flac"),
    ("avi", "avi", "libmp3lame", "mpeg4"), ("wmv", "asf", "wmav2", "wmv2"),
    ("asf", "asf", "wmav2"), ("flv", "flv", "libmp3lame", "flv"),
    ("mpg", "mpeg", "mp2", "mpeg1video"), ("mpeg", "mpeg", "mp2", "mpeg1video"),
    ("ts", "mpegts", "aac", "mpeg2video"), ("m2ts", "mpegts", "ac3", "mpeg2video"),
    ("mts", "mpegts", "ac3", "mpeg2video"), ("vob", "vob", "ac3", "mpeg2video"),
    ("3gp", "3gp", "aac", "mpeg4"), ("3g2", "3g2", "aac", "mpeg4"),
    ("ogv", "ogg", "libvorbis", "libtheora"),
]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    dest = parser.parse_args().directory
    dest.mkdir(parents=True, exist_ok=True)
    for ext, container, audio, *video in FORMATS:
        cmd = ["ffmpeg", "-y", "-nostdin", "-v", "error"]
        if video:
            cmd += ["-f", "lavfi", "-i", "color=c=0x303040:s=32x32:r=25:d=0.3"]
        rate = {"amr": 8000, "flv": 44100}.get(ext, 48000)
        cmd += ["-f", "lavfi", "-i", f"sine=frequency=770:sample_rate={rate}:duration=0.3", "-c:a", audio, "-threads", "1"]
        if video:
            cmd += ["-c:v", video[0]]
        if ext == "dts":
            cmd += ["-strict", "-2"]
        if ext == "amr":
            cmd += ["-b:a", "12.2k"]
        if ext in {"mts", "m2ts"}:
            cmd += ["-mpegts_m2ts_mode", "1"]
        subprocess.run(cmd + ["-map_metadata", "-1", "-f", container, str(dest / f"Format {ext.upper()}.{ext}")], check=True)

    # FFmpeg decodes Monkey's Audio but does not encode it. Use its published FATE test sample.
    with urllib.request.urlopen("https://fate-suite.ffmpeg.org/lossless-audio/luckynight-mac388-c2000.ape", timeout=30) as response:
        ape = response.read(328001)
    if hashlib.sha256(ape).hexdigest() != "73d7dd1e452e2f02745e00e90a501a7f4612fde2b3448fcf9dfa3fa692aef42a":
        raise ValueError("Unexpected APE fixture checksum")
    (dest / "Format APE.ape").write_bytes(ape)
    print(f"Wrote {len(FORMATS) + 1} formats to {dest}")


if __name__ == "__main__":
    main()
