$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'artifacts/codecs'
$null = New-Item -ItemType Directory -Force $out
$version = '9.0.2'
$archive = "ffmpeg-$version.tar.xz"
$sourceHash = '8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e'
$toolName = 'w64devkit-x64-2.11.0.7z.exe'
$toolHash = '54c2bb9962e8a30c8270e34c5efc3d5fee579f53087fa7d435b95fbba2af7b2b'
$recipe = Join-Path $PSScriptRoot 'ffmpeg.sh'
$stamp = Join-Path $out 'build-id'
$id = "$sourceHash $toolHash $((Get-FileHash $recipe).Hash) $((Get-FileHash $PSCommandPath).Hash)"
$exe = Join-Path $out 'ffmpeg.exe'
$source = Join-Path $out $archive
if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq $id -and (Test-Path $exe) -and
    (Test-Path $source) -and (Get-FileHash $source).Hash -eq $sourceHash) { return }

function Get-VerifiedFile($url, $path, $sha) {
    curl.exe --fail --silent --show-error --location --retry 3 $url --output $path
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $path).Hash -ne $sha) { throw "Could not verify $url" }
}

$work = [IO.Directory]::CreateTempSubdirectory('SoundboardCodec-').FullName
$savedPath = $env:Path
try {
    Get-VerifiedFile "https://ffmpeg.org/releases/$archive" (Join-Path $work $archive) $sourceHash
    Get-VerifiedFile "https://github.com/skeeto/w64devkit/releases/download/v2.11.0/$toolName" (Join-Path $work $toolName) $toolHash
    foreach ($name in @($archive, $toolName)) {
        tar.exe -xf (Join-Path $work $name) -C $work
        if ($LASTEXITCODE -ne 0) { throw "Could not extract $name" }
    }
    $bin = Join-Path $work 'w64devkit/bin'
    $env:Path = "$bin;$savedPath"
    $src = Join-Path $work "ffmpeg-$version"
    $jobs = [Math]::Min([Environment]::ProcessorCount, 12)
    & (Join-Path $bin 'sh.exe') $recipe.Replace('\','/') $src.Replace('\','/') "$jobs" *> (Join-Path $work 'build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $work 'build.log') -Tail 50; throw 'Audio codec build failed.' }
    Copy-Item -LiteralPath (Join-Path $src 'ffmpeg.exe') -Destination $exe -Force
    Copy-Item -LiteralPath (Join-Path $work $archive) -Destination $source -Force
    Copy-Item -LiteralPath (Join-Path $src 'COPYING.LGPLv2.1') -Destination (Join-Path $out 'FFmpeg-LGPL-2.1.txt') -Force
    $id | Set-Content -LiteralPath $stamp
    Write-Host "Built FFmpeg $version audio importer ($((Get-Item $exe).Length) bytes)."
} finally {
    $env:Path = $savedPath
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
    if ([IO.Path]::GetDirectoryName($work) -eq $tempRoot -and [IO.Path]::GetFileName($work).StartsWith('SoundboardCodec-')) {
        [IO.Directory]::Delete($work, $true)
    }
}
