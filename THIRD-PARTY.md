# Third-party software

Soundboard's code and icons use the [MIT license](LICENSE).

The six starter pads use recordings dedicated to the public domain under [CC0 1.0](licenses/CC0-1.0.txt). The bundled edits are also CC0. They have been trimmed, mixed to mono and mastered for voice chat. The [mastering script](scripts/starter-sounds.py) identifies the exact source files and checks their hashes.

| Pad | Recording |
| --- | --- |
| Air horn | “Airhorn” by jacksonacademyashmore. [Freesound](https://freesound.org/people/jacksonacademyashmore/sounds/414208/) |
| Quick applause | “Crowd.Yay.Applause.25ppl.Short.wav” by jessepash. [Freesound](https://freesound.org/people/jessepash/sounds/139972/) |
| Coin drop | One coin spinning and landing from “coin(s) spin drop.wav” by Yuval. [Freesound](https://freesound.org/people/Yuval/sounds/197214/) |
| Glass ping | One ringing glass from “Wine glass tinkles” by jhumbucker. [Freesound](https://freesound.org/people/jhumbucker/sounds/193823/) |
| Power down | “zapThreeToneDown” from “63 Digital sound effects” by Kenney. [OpenGameArt](https://opengameart.org/content/63-digital-sound-effects-lasers-phasers-space-etc) |
| Signal check | The spoken numbers one, two and three from Kenney's “Voiceover Pack.” [OpenGameArt](https://opengameart.org/content/voiceover-pack-40-lines) |

* The interface uses the unmodified Inter 4.1 typeface. The font files and their SIL Open Font License are under [Assets/Fonts](src/Soundboard/Assets/Fonts/OFL.txt). The portable archive includes the license in `Assets/Fonts/OFL.txt`. [Inter project](https://github.com/rsms/inter/releases/tag/v4.1)
* The audio bridge implements the Voicemeeter Remote output-insert ABI. Potato and its Remote DLL come from the user's Voicemeeter installation. The SDK notice is included in [licenses/Voicemeeter-SDK.txt](licenses/Voicemeeter-SDK.txt). [VB-Audio SDK](https://github.com/vburel2018/Voicemeeter-SDK)
* Portable builds include Microsoft's .NET and Windows Desktop runtimes. Their applicable license files are copied from the resolved runtime packs into the archive's `licenses` folder.
* The sound grid uses VirtualizingWrapPanel 2.5.4 under the [MIT license](licenses/VirtualizingWrapPanel.txt). [Upstream project](https://github.com/sbaeumlisberger/VirtualizingWrapPanel/tree/v2.5.4)
* The portable app runs FFmpeg 9.0.2 as a separate import process. Its audio-only build uses the LGPL 2.1 or later. Every CI artifact and tagged release includes the matching, unmodified `ffmpeg-9.0.2.tar.xz` source archive alongside the app ZIP. The ZIP includes the license and build recipe in `licenses`. [FFmpeg](https://ffmpeg.org/) · [Build configuration](scripts/ffmpeg.sh)
* The FFmpeg executable uses the [MinGW-w64 runtime](licenses/MinGW-w64-runtime.txt).

Raycast is the visual reference for the launcher. Soundboard is an independent project.
