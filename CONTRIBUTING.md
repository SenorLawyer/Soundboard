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

The starter WAVs are checked in. To rebuild them, run `py -3 scripts/starter-sounds.py` with a full FFmpeg on PATH. The current files were mastered with FFmpeg 8.0.1. Keep the 70% mono send close to the rest of the pack and measure peaks after encoding as well as before. The current pack measures between −18.8 and −17.1 LUFS on a 70% mono send.

Windows CI tests the extracted application with two local WebRTC peers. The check measures mono loudness and true peak, verifies that private preview adds nothing to B1, and sends the native B1 mix through Opus in three modes: open mic, voice activity, and speech noise suppression with voice activity. It opens no audio device. To run it locally, use a Python 3.12 or 3.13 virtual environment:

```powershell
python -m pip install -r tests/voice-requirements.txt
dotnet run --project tests/Checks -c Release -- --import-proof artifacts/voice-import-proof
python tests/voice-loopback.py artifacts/Windows-x64 --import-proof artifacts/voice-import-proof
```

Optionally install `vosk` and add `--speech-model path/to/vosk-model-small-en-us-0.15` to transcribe the received Signal check. [Vosk English model](https://alphacephei.com/vosk/models)

The import fixture starts with the spoken count attenuated by 24 dB. CI drops it through the Windows OLE target and feeds the resulting library WAV to the receiver check. The local command above exercises the same import and catalog code without opening a window. Imported copies target −15 LUFS before the master volume, with at most 30 dB of boost and a stereo peak limiter.

The check downloads a hash-pinned 300 KB RNNoise test model. Pass `--noise-model path/to/cb.rnnn` to use an offline copy. Discord's proprietary Krisp filter can give different results. [RNNoise test model](https://github.com/GregorR/rnnoise-models/blob/3eee541a283fd3b8f81b85b1748e3b9ccbefa04d/conjoined-burgers-2018-08-28/cb.rnnn)

For a hardware check, route a microphone to B1 and leave the music strip's B1 send off. Play one file while capturing **Voicemeeter Out B1**. Check that the clip and microphone reach the capture. Preview the same clip on A1 or A2 and check that B1 only carries its original mix.

## Pull requests

Branch from `main`. Describe the behavior change and how you tested it. Include a screenshot for a visible UI change. The protected branch requires an up-to-date passing Windows check, resolved conversations and a squash merge. The maintainer can merge a solo-authored change after those checks pass.

Contributions use the project's MIT license. For sampled audio, fonts, or artwork, identify the source and include the applicable redistribution license.
