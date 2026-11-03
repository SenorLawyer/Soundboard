# Contributing

Open an issue for a change to routing or keyboard behavior before writing a large patch. A small fix can go straight to a pull request.

## Build and test

Use Windows x64, PowerShell 7.3 or later, the .NET 10 SDK, and Visual Studio 2022 or later with **Desktop development with C++** and a Windows SDK. Run this from the repository root.

```powershell
./scripts/build.ps1
```

The first build downloads hash-pinned FFmpeg source and a portable GCC toolchain. It builds the local import helper once and reuses it while its recipe is unchanged. The Windows checks exercise all 64 mixer channels, actual media decoding, OLE file drops and keyboard navigation. The synthetic media in `tests/Checks/Media` was generated from a sine tone and a solid-color video frame. Add `-Package` to produce the self-contained Windows archive, corresponding FFmpeg source archive and checksums. The tests use a temporary library and run without Potato.

## Audio changes

To repeat the full file-drop check, generate the corpus with Python 3 and a full FFmpeg executable on your PATH. The generator uses synthetic tones and video frames plus one checksum-verified APE sample from FFmpeg's FATE suite.

```powershell
py -3 tests/generate-format-fixtures.py artifacts/format-corpus
dotnet run --project tests/Checks -c Release -- --formats artifacts/format-corpus
```

The callback runs on Potato's audio thread. Keep allocation, disk access, managed calls and blocking waits out of it. Preserve existing audio on every bus. Add a focused test when changing a channel offset, playback lifetime, or sample conversion.

The starter WAVs are checked in. To rebuild them, run `py -3 scripts/starter-sounds.py` with a full FFmpeg on PATH. Keep the 70% mono send close to the rest of the pack and measure peaks after encoding as well as before. The current pack measures between −18.8 and −17.1 LUFS on a 70% mono send.

To test a sound pack during a call, use the local receiver check. It runs the app's native mixer in memory and sends its B1 samples through Opus between two WebRTC peers on the same PC. It tests open mic and WebRTC voice activity, and checks that private preview adds nothing to B1. It opens no audio device. In a Python virtual environment, install `numpy aiortc webrtcvad-wheels`, then run:

```powershell
python tests/voice-loopback.py artifacts/Windows-x64
```

Optionally install `vosk` and add `--speech-model path/to/vosk-model-small-en-us-0.15` to transcribe the received Signal check. [Vosk English model](https://alphacephei.com/vosk/models)

Add `--noise-model path/to/cb.rnnn` with a full FFmpeg on PATH to run a third call through a neural speech filter before voice activation. This makes it possible to reproduce how much of an effect a speech filter can remove. Discord's proprietary Krisp filter can give different results. [RNNoise test model](https://github.com/GregorR/rnnoise-models/blob/3eee541a283fd3b8f81b85b1748e3b9ccbefa04d/conjoined-burgers-2018-08-28/cb.rnnn)

For a hardware check, route a microphone to B1 and leave the music strip's B1 send off. Play one file while capturing **Voicemeeter Out B1**. Check that the clip and microphone reach the capture. Preview the same clip on A1 or A2 and check that B1 only carries its original mix.

## Pull requests

Branch from `main`. Describe the behavior change and how you tested it. Include a screenshot for a visible UI change. The protected branch requires an up-to-date passing Windows check, resolved conversations and a squash merge. The maintainer can merge a solo-authored change after those checks pass.

Contributions use the project's MIT license. For sampled audio, fonts, or artwork, identify the source and include the applicable redistribution license.
