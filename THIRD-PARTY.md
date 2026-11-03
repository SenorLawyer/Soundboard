# Third-party software

Soundboard's code, icons and six synthesized starter clips use the [MIT license](LICENSE).

* The interface uses the unmodified Inter 4.1 typeface. The font files and their SIL Open Font License are under [Assets/Fonts](src/Soundboard/Assets/Fonts/OFL.txt). The portable archive includes the license in `Assets/Fonts/OFL.txt`. [Inter project](https://github.com/rsms/inter/releases/tag/v4.1)
* The audio bridge implements the Voicemeeter Remote output-insert ABI. Potato and its Remote DLL come from the user's Voicemeeter installation. The SDK notice is included in [licenses/Voicemeeter-SDK.txt](licenses/Voicemeeter-SDK.txt). [VB-Audio SDK](https://github.com/vburel2018/Voicemeeter-SDK)
* Portable builds include Microsoft's .NET and Windows Desktop runtimes. Their applicable license files are copied from the resolved runtime packs into the archive's `licenses` folder.
* The sound grid uses VirtualizingWrapPanel 2.5.4 under the [MIT license](licenses/VirtualizingWrapPanel.txt). [Upstream project](https://github.com/sbaeumlisberger/VirtualizingWrapPanel/tree/v2.5.4)
* The portable app runs FFmpeg 9.0.2 as a separate import process. Its audio-only build uses the LGPL 2.1 or later. Every CI artifact and tagged release includes the matching, unmodified `ffmpeg-9.0.2.tar.xz` source archive alongside the app ZIP. The ZIP includes the license and build recipe in `licenses`. [FFmpeg](https://ffmpeg.org/) · [Build configuration](scripts/ffmpeg.sh)
* The FFmpeg executable uses the [MinGW-w64 runtime](licenses/MinGW-w64-runtime.txt).

Raycast is the visual reference for the launcher. Soundboard is an independent project.
