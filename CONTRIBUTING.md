# Contributing

Open an issue for a change to routing or keyboard behavior before writing a large patch. A small fix can go straight to a pull request.

## Build and test

Use Windows x64, the .NET 10 SDK, and Visual Studio 2022 or later with **Desktop development with C++** and a Windows SDK. From PowerShell at the repository root, run:

```powershell
./scripts/build.ps1
```

This compiles the native mixer with warnings treated as errors, checks each of Potato's 64 output channels, builds the app, and runs the library and Windows decoder tests. Add `-Package` to produce the self-contained Windows archive. The tests use a temporary library. They run without Potato.

## Audio changes

The callback runs on Potato's audio thread. Keep allocation, disk access, managed calls and blocking waits out of it. Preserve existing audio on every bus. Add a focused test when changing a channel offset, playback lifetime, or sample conversion.

For a hardware check, route a microphone to B1 and leave the music strip's B1 send off. Play one file while capturing **Voicemeeter Out B1**. Check that the clip and microphone reach the capture. Preview the same clip on A1 or A2 and check that B1 only carries its original mix.

## Pull requests

Branch from `main`. Describe the behavior change and how you tested it. Include a screenshot for a visible UI change. The protected branch requires an up-to-date passing Windows check, resolved conversations and a squash merge. The maintainer can merge a solo-authored change after those checks pass.

Contributions use the project's MIT license. For sampled audio, fonts, or artwork, identify the source and include the applicable redistribution license.
