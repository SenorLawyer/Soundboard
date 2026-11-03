"""Master the bundled CC0 recordings. Requires Python 3 and a full FFmpeg on PATH."""
import hashlib
import io
import subprocess
import tempfile
import urllib.request
import wave
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
RATE = 48000
SOURCES = {
    "horn": ("https://cdn.freesound.org/previews/414/414208_6938106-hq.mp3",
             "9407b48ceef31faff0ca2fd66c9ba82f64f2b31c9e4862a20302937569534983"),
    "crowd": ("https://cdn.freesound.org/previews/139/139972_968325-hq.mp3",
              "51a08430d441f5c29fef03b2820f0650179b9b449a965e9beb2b379f14b100f8"),
    "coin": ("https://cdn.freesound.org/previews/197/197214_770707-hq.mp3",
             "8b9132dca4668c13a728953e8510435799fcba06d496e4120e6705a006f2c439"),
    "glass": ("https://cdn.freesound.org/previews/193/193823_208303-hq.mp3",
              "35ed473bc81c37165758c2185f21c7a21c2b92bb1d70e4890a379092bd7a7b6c"),
    "digital": ("https://opengameart.org/sites/default/files/Digital_SFX_Set.zip",
                "022f0f4b73da55989567fe2b373c8af89d74f5d015257e293cab497ca606d063"),
    "voice": ("https://opengameart.org/sites/default/files/Voiceover%20Pack.zip",
              "2ff0c8c51a959a754df698c4ee1e7c2db007c4abde4d600fd19545a1aea9d835"),
}


def ffmpeg(args, data=None):
    return subprocess.run(["ffmpeg", "-nostdin", "-v", "error", *args, "pipe:1"],
                          input=data, stdout=subprocess.PIPE, check=True).stdout


def main():
    dest = ROOT / "src/Soundboard/Assets/Sounds"
    with tempfile.TemporaryDirectory() as scratch:
        inputs = {}
        for key, (url, digest) in SOURCES.items():
            request = urllib.request.Request(url, headers={"User-Agent": "Soundboard starter audio builder"})
            with urllib.request.urlopen(request, timeout=30) as response:
                source = response.read()
            if hashlib.sha256(source).hexdigest() != digest:
                raise ValueError(f"Upstream recording changed: {key}")
            inputs[key] = source

        def take(key, start, end, member=None):
            data = inputs[key]
            if member:
                with zipfile.ZipFile(io.BytesIO(data)) as archive:
                    data = archive.read(member)
            # Seekable input lets the decoder honor the original encoder padding.
            path = Path(scratch) / (Path(member).name if member else key + ".mp3")
            path.write_bytes(data)
            return ffmpeg(["-i", str(path), "-af", f"atrim=start={start}:end={end},aresample={RATE}",
                           "-ac", "1", "-f", "f32le"])

        def rest(seconds):
            return bytes(round(seconds * RATE) * 4)

        sounds = [
            (take("horn", 0, 1.58), 0.5, 0.06),
            (take("crowd", 0.20, 2.95), 6.5, 0.18),
            (take("coin", 42.88, 44.17), 16.7, 0.035),
            (take("glass", 7.28, 8.50), 13.8, 0.08),
            (take("digital", 0.035, 1.115, "zapThreeToneDown.mp3"), 17.8, 0.06),
            (rest(0.135).join(take("voice", 0, end, f"Female/{i}.ogg")
                              for i, end in enumerate((0.49, 0.58, 0.65), 1)), 0.25, 0.025),
        ]
        for i, (mono, gain_db, tail) in enumerate(sounds, 1):
            duration = len(mono) / (4 * RATE)
            chain = (f"highpass=f=65,afade=d=0.004,afade=t=out:st={duration-tail:.6f}:d={tail},"
                     f"volume={gain_db}dB,alimiter=limit=0.77:attack=4:release=60:level=0:latency=1,"
                     "pan=stereo|c0=c0|c1=c0")
            stereo = ffmpeg(["-f", "f32le", "-ar", str(RATE), "-ac", "1", "-i", "pipe:0",
                             "-af", chain, "-f", "s16le"], mono)
            path = dest / f"{i:08x}{'0' * 24}.wav"
            with wave.open(str(path), "wb") as out:
                out.setparams((2, 2, RATE, 0, "NONE", "not compressed"))
                out.writeframes(stereo)
            print(f"{path.name}: {round(len(stereo) / (4 * RATE) * 1000)} ms")


if __name__ == "__main__":
    main()
