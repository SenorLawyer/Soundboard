param([switch]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    & "$PSScriptRoot/codecs.ps1"
    & "$PSScriptRoot/native.ps1" -Configuration Release
    dotnet build src/Soundboard -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed.' }
    dotnet run --project tests/Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Library and decoder checks failed.' }
    dotnet run --project tests/Checks -c Release --no-build -- --drop "$root/artifacts/voice-import-proof"
    if ($LASTEXITCODE -ne 0) { throw 'Windows file-drop check failed.' }
    if ($Package) {
        # A new staging directory keeps stale runtime files out of each archive.
        $stage = Join-Path $root "artifacts/stage-$([Guid]::NewGuid().ToString('N'))"
        dotnet publish src/Soundboard -c Release -r win-x64 --self-contained true -o $stage --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
        Copy-Item -LiteralPath 'LICENSE','README.md','THIRD-PARTY.md','licenses','docs' -Destination $stage -Recurse
        Copy-Item -LiteralPath 'artifacts/codecs/FFmpeg-LGPL-2.1.txt','scripts/ffmpeg.sh','scripts/codecs.ps1' -Destination "$stage/licenses"
        $source = Get-Item -LiteralPath 'artifacts/codecs/ffmpeg-9.0.2.tar.xz'
        Copy-Item -LiteralPath $source.FullName -Destination "$root/artifacts" -Force
        $packs = dotnet msbuild src/Soundboard/Soundboard.csproj -t:ResolveFrameworkReferences -getItem:ResolvedRuntimePack -p:SelfContained=true -p:Configuration=Release
        if ($LASTEXITCODE -ne 0) { throw 'Could not locate runtime notices.' }
        foreach ($pack in ($packs | Out-String | ConvertFrom-Json).Items.ResolvedRuntimePack) {
            if ($pack.FrameworkName -in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
                Get-ChildItem -LiteralPath $pack.PackageDirectory -File | Where-Object Name -Match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' | ForEach-Object {
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage "licenses/$($pack.FrameworkName)-$($_.Name)")
                }
            }
        }
        $zip = Join-Path $root 'artifacts/Soundboard-win-x64.zip'
        Compress-Archive -Path "$stage/*" -DestinationPath $zip -Force
        $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $sourceHash = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        @("$hash  Soundboard-win-x64.zip", "$sourceHash  $($source.Name)") | Set-Content -LiteralPath "$root/artifacts/SHA256SUMS.txt" -Encoding ascii
        Write-Host "Portable executable: $stage/Soundboard.exe"
        Write-Host "Archive: $zip ($hash)"
    }
} finally { Pop-Location }
