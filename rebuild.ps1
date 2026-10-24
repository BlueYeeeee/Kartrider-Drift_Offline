param([string] $OutputDirectory = (Join-Path $PSScriptRoot 'build/client'), [switch] $Tests)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$nativeOutput = Join-Path $PSScriptRoot 'build/native'
& (Join-Path $PSScriptRoot 'native/build.ps1') -OutputDirectory $nativeOutput -Tests:$Tests
$runtime = Join-Path $output 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeOutput 'KdLocalNative.dll') -Destination $runtime -Force
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$bundle = Join-Path $output 'LocalData.zip'
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle }
$resourceRoot = Join-Path $PSScriptRoot 'resources'
$archive = [System.IO.Compression.ZipFile]::Open($bundle, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $resourceRoot -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($resourceRoot.Length + 1).Replace('\', '/')
        if ($entryName -eq 'release/manifest.json') {
            $manifest = [IO.File]::ReadAllText($_.FullName) | ConvertFrom-Json
            $manifest | Add-Member -NotePropertyName nativeSha256 -NotePropertyValue ((Get-FileHash (Join-Path $runtime 'KdLocalNative.dll') -Algorithm SHA256).Hash.ToLowerInvariant()) -Force
            $manifest.mode = 'local single-player native compatibility prerelease'
            $manifest.acceptance = 'Native contracts and bounded local login are separate from race acceptance; no Frida runtime'
            $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
            $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write(($manifest | ConvertTo-Json -Depth 8 -Compress)) } finally { $writer.Dispose() }
        } else {
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $archive.Dispose() }
$compileArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
 '/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
 '/r:System.Windows.Forms.dll','/r:System.Drawing.dll',
 ('/out:' + (Join-Path $output 'KartDriftLocal.exe')),
 ('/resource:' + $bundle + ',LocalData.zip'))
$compileArgs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'launcher-csharp') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'C# compile failed' }
if ($output.TrimEnd('\') -ne $PSScriptRoot.TrimEnd('\')) {
    foreach ($name in @('Localization','TrialTower')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output -Recurse -Force }
}
$licenses = Join-Path $output 'licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/vendor/minhook/LICENSE.txt') -Destination (Join-Path $licenses 'MinHook-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'native/vendor/nlohmann/LICENSE.MIT') -Destination (Join-Path $licenses 'nlohmann-json-LICENSE.MIT') -Force
if ($output.TrimEnd('\') -ne $PSScriptRoot.TrimEnd('\')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $output -Force }
