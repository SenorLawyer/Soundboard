param([switch]$Package)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
Push-Location $root
try {
    & "$PSScriptRoot/native.ps1" -Configuration Release
    dotnet build src/Soundboard -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows application build failed.' }
    dotnet run --project tests/Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Library and decoder checks failed.' }
    dotnet run --project tests/Checks -c Release --no-build -- --drop
    if ($LASTEXITCODE -ne 0) { throw 'Windows file-drop check failed.' }
    if ($Package) {
        # A new staging directory keeps stale runtime files out of each archive.
        $stage = Join-Path $root "artifacts/stage-$([Guid]::NewGuid().ToString('N'))"
        dotnet publish src/Soundboard -c Release -r win-x64 --self-contained true -o $stage --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
        Copy-Item -LiteralPath 'LICENSE','README.md','THIRD-PARTY.md','licenses','docs' -Destination $stage -Recurse
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
        "$hash  Soundboard-win-x64.zip" | Set-Content -LiteralPath "$root/artifacts/SHA256SUMS.txt" -Encoding ascii
        Write-Host "Portable executable: $stage/Soundboard.exe"
        Write-Host "Archive: $zip ($hash)"
    }
} finally { Pop-Location }
