# Soundboard

Drop audio or video files onto the board. Each becomes a sound pad you can click to play through your microphone. Soundboard mixes clips directly into Voicemeeter Potato's **Out B1** alongside your mic. Your music and game chat keep their existing routes.

![Soundboard on Windows](docs/soundboard.png)

Watch the [Windows walkthrough and screenshot gallery](https://github.com/SenorLawyer/Soundboard/pull/1#design-walkthrough).

## Get started

1. Start [Voicemeeter Potato](https://vb-audio.com/Voicemeeter/potato.htm) on x64 Windows 10 or 11. In your call or game, select **Voicemeeter Out B1** as the microphone. Older driver versions call it **Voicemeeter Output**.
2. Extract the entire `Soundboard-win-x64.zip` archive and open `Soundboard.exe`. Tagged builds appear under [Releases](https://github.com/SenorLawyer/Soundboard/releases). Each successful pull request build also has a downloadable Windows artifact.
3. Click a pad to send its sound to B1. Click its headphones to preview privately, or its star to add it to Favorites. Right-click for more actions. **Out B1** in the footer lets you choose your A1 or A2 preview device, set the clip volume, or hear a clip locally while sending it.

Keep B1 enabled on the physical mic strip. Any strip you already send to B1 will still reach the call. Push-to-talk and voice filtering in the receiving app also apply to clips.

## Keyboard

| Action | Shortcut |
| --- | --- |
| Show or hide the launcher from any app | Ctrl+Alt+Space, or Ctrl+Shift+Space if the first shortcut is in use |
| Stop playback from any app | Ctrl+Alt+Backspace |
| Find a clip | Type to search, or Ctrl+F |
| Move between pads | ↑ and ↓ change rows, ← and → change columns |
| Play or stop the selected clip | Enter |
| Preview on the selected hardware bus | Ctrl+P |
| Open the searchable action panel | Ctrl+K |
| Import files | Ctrl+O |
| Toggle favorite / rename | Ctrl+D / F2 |
| Play a clip from a game | Assign Ctrl+Alt+1 through 9 in Actions |

Esc closes the current panel, stops playback, clears the search, or hides the window, in that order. The tray icon shows the active launcher shortcut and has Open and Quit commands. If both shortcuts are taken, use the tray icon to open Soundboard.

When editing a search, Left and Right move the caret. Up or Down moves focus into the pad grid.

## Files and routing

Drag several files or a folder onto the board, or use **Import**. A folder imports supported files directly inside it. Click the progress control to cancel a batch and keep the completed pads. Imports run in local decoder processes that close on cancellation. Playback reads the decoded copy. Each source can be up to 256 MiB with up to five minutes of audio.

| Source | Examples |
| --- | --- |
| Music and voice notes | MP3, MP2, AAC, M4A/M4B/M4R, Ogg Vorbis, Opus, WMA, AMR |
| Lossless and production audio | WAV, Wave64, AIFF/AIFC, FLAC, ALAC in M4A, WavPack, APE, TTA, CAF, AU |
| Video and surround audio | MP4, MOV, 3GP/3G2, WebM, MKV/MKA, AVI, WMV, FLV, OGV, MPEG, VOB, TS/MTS/M2TS, AC-3, E-AC-3, DTS |

For video, the importer reads the first audio track. Files need a decodable audio track and must be free of DRM.

The library and its decoded copies live in `%LOCALAPPDATA%\Soundboard`. You can move the original after import. Search, favorites, shortcuts and playback run locally.

The native output insert adds the clip to the front left and right channels of B1 before Potato's master fader. It takes the insert only during playback and releases it afterward. If another insert effect owns that slot, Soundboard names it in the error message. A1 and A2 always follow the devices assigned inside Potato.

## Development

Run `./scripts/build.ps1` on Windows to build and test, or add `-Package` for a self-contained archive and SHA-256 checksum. The app uses WPF on .NET 10 and a small C++ audio DLL. See the [contributor guide](CONTRIBUTING.md) for prerequisites and hardware checks.

## License

Soundboard and its original starter sounds are [MIT licensed](LICENSE). The [third-party notices](THIRD-PARTY.md) cover the font, grid, codec helper and runtimes.
