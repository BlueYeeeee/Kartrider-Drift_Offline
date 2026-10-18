$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$bundle = Join-Path $PSScriptRoot 'LocalData.zip'
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle }
$resourceRoot = Join-Path $PSScriptRoot 'resources'
$archive = [System.IO.Compression.ZipFile]::Open($bundle, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $resourceRoot -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($resourceRoot.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
$compileArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+','/codepage:65001',
 '/r:System.Web.Extensions.dll','/r:System.IO.Compression.dll','/r:System.IO.Compression.FileSystem.dll',
 '/r:System.Windows.Forms.dll','/r:System.Drawing.dll',
 ('/out:' + (Join-Path $PSScriptRoot 'KartDriftLocal.exe')),
 ('/resource:' + (Join-Path $PSScriptRoot 'LocalData.zip') + ',LocalData.zip'))
$compileArgs += @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'launcher-csharp') -Filter '*.cs' | ForEach-Object { $_.FullName })
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'C# compile failed' }
