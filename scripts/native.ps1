param([ValidateSet('Release','Debug')] [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root "artifacts/native/$Configuration"
$null = New-Item -ItemType Directory -Force $out
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Install Visual Studio Build Tools with the Desktop development with C++ workload.' }
Import-Module (Join-Path $vs 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll')
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$opts = @('/nologo','/std:c++20','/EHsc','/utf-8','/W4','/WX','/MT','/D_CRT_SECURE_NO_WARNINGS','/DNOMINMAX','/Brepro')
if ($Configuration -eq 'Release') { $opts += '/O2' } else { $opts += '/Od' }
Push-Location $out
try {
    & cl @opts /LD "$root/native/mixer.cpp" "$root/native/decode.cpp" '/Fe:Soundboard.Audio.dll' /link '/IMPLIB:Soundboard.Audio.lib' '/DYNAMICBASE' '/NXCOMPAT' '/DELAYLOAD:mfplat.dll' '/DELAYLOAD:mfreadwrite.dll' 'delayimp.lib' 'mfplat.lib' 'mfreadwrite.lib' 'mfuuid.lib' 'ole32.lib'
    if ($LASTEXITCODE -ne 0) { throw 'Native audio build failed.' }
    & cl @opts "$root/tests/mixer.cpp" '/Fe:mixer-tests.exe' 'Soundboard.Audio.lib'
    if ($LASTEXITCODE -ne 0) { throw 'Mixer test build failed.' }
    & "./mixer-tests.exe"
    if ($LASTEXITCODE -ne 0) { throw 'Mixer tests failed.' }
} finally { Pop-Location }
