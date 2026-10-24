param([Parameter(Mandatory=$true)][string] $OutputDirectory, [switch] $Tests)
$ErrorActionPreference = 'Stop'
# Dependencies are vendored at fixed upstream releases. Building needs no network.
$sources = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'vendor/SOURCES.json')) | ConvertFrom-Json
foreach ($property in $sources.PSObject.Properties) {
    $path = Join-Path $PSScriptRoot ('vendor/' + $property.Name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value.sha256) { throw ('Vendored source hash mismatch: ' + $property.Name) }
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$objects = Join-Path $output 'obj'
New-Item -ItemType Directory -Path $objects -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual C++ x64 build tools are required.' }
$vc = (Get-ChildItem (Join-Path $vs 'VC/Tools/MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10'
$sdk = (Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
$compiler = Join-Path $vc 'bin/Hostx64/x64/cl.exe'
$assembler = Join-Path $vc 'bin/Hostx64/x64/ml64.exe'
$linker = Join-Path $vc 'bin/Hostx64/x64/link.exe'
$includes = @(('/I' + (Join-Path $vc 'include')))
foreach ($folder in @('ucrt','shared','um')) { $includes += '/I' + (Join-Path $sdkRoot ('Include/' + $sdk + '/' + $folder)) }
$libraries = @(('/LIBPATH:' + (Join-Path $vc 'lib/x64')), ('/LIBPATH:' + (Join-Path $sdkRoot ('Lib/' + $sdk + '/ucrt/x64'))), ('/LIBPATH:' + (Join-Path $sdkRoot ('Lib/' + $sdk + '/um/x64'))))
$core = @('vendor/minhook/src/buffer.c','vendor/minhook/src/hook.c','vendor/minhook/src/trampoline.c','vendor/minhook/src/hde/hde64.c')
$compiled = @()
foreach ($source in $core) {
    $name = [IO.Path]::GetFileNameWithoutExtension($source)
    $object = Join-Path $objects ($name + '.obj')
    & $compiler /nologo /c /O2 /MT /W3 @includes ('/Fo' + $object) (Join-Path $PSScriptRoot $source)
    if ($LASTEXITCODE -ne 0) { throw ('Native dependency compile failed: ' + $source) }
    $compiled += $object
}
$hooks = Join-Path $objects 'hooks.obj'
& $assembler /nologo /c ('/Fo' + $hooks) (Join-Path $PSScriptRoot 'hooks.asm')
if ($LASTEXITCODE -ne 0) { throw 'Native ABI thunk assembly failed' }
$compat = Join-Path $objects 'compat.obj'
& $compiler /nologo /c /O2 /MT /W4 /EHsc /std:c++17 /utf-8 @includes ('/Fo' + $compat) (Join-Path $PSScriptRoot 'compat.cpp')
if ($LASTEXITCODE -ne 0) { throw 'Native compatibility compile failed' }
& $linker /NOLOGO /DLL /MACHINE:X64 /OPT:REF /OPT:ICF /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT ('/OUT:' + (Join-Path $output 'KdLocalNative.dll')) @libraries $compat $hooks @compiled kernel32.lib user32.lib advapi32.lib
if ($LASTEXITCODE -ne 0) { throw 'Native compatibility link failed' }
if ($Tests) {
    $contracts = Join-Path $objects 'contracts.obj'
    & $compiler /nologo /c /O2 /MT /W4 /EHsc /std:c++17 /utf-8 @includes ('/Fo' + $contracts) (Join-Path $PSScriptRoot 'contracts.cpp')
    if ($LASTEXITCODE -ne 0) { throw 'Native contracts compile failed' }
    $fixtures = Join-Path $objects 'fixtures.obj'
    & $assembler /nologo /c ('/Fo' + $fixtures) (Join-Path $PSScriptRoot 'fixtures.asm')
    if ($LASTEXITCODE -ne 0) { throw 'Native fixtures assembly failed' }
    & $linker /NOLOGO /MACHINE:X64 /SUBSYSTEM:CONSOLE /OPT:REF /OPT:ICF ('/OUT:' + (Join-Path $output 'NativeContracts.exe')) @libraries $contracts $hooks $fixtures @compiled kernel32.lib user32.lib advapi32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native contracts link failed' }
    $probe = Join-Path $objects 'probe.obj'
    & $compiler /nologo /c /O2 /MT /W4 /EHsc /std:c++17 /utf-8 @includes ('/Fo' + $probe) (Join-Path $PSScriptRoot 'sdk-probe.cpp')
    if ($LASTEXITCODE -ne 0) { throw 'Native SDK probe compile failed' }
    & $linker /NOLOGO /MACHINE:X64 /SUBSYSTEM:CONSOLE /OPT:REF /OPT:ICF ('/OUT:' + (Join-Path $output 'NativeSdkProbe.exe')) @libraries $probe kernel32.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native SDK probe link failed' }
}
