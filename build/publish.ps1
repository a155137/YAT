<#
.SYNOPSIS
    Publishes the YAT release for Windows x64 (Task #048).

.DESCRIPTION
    Builds src/YAT.App self-contained for win-x64 - not single-file, not trimmed, without ReadyToRun and without debug
    symbols - and lays the release out under artifacts/:

        artifacts/YAT-v<version>-win-x64/      YAT.exe, the .NET runtime, the managed and native dependencies,
                                               LICENSE.txt, README.txt, THIRD-PARTY-NOTICES.txt and licenses/
        artifacts/YAT-v<version>-win-x64.zip   that folder, zipped

    <version> is the one in Directory.Build.props, asked of MSBuild; README.txt and THIRD-PARTY-NOTICES.txt come from
    build/docs, LICENSE.txt from the repository root. The release folder and zip of that version are replaced; nothing
    else is touched. Stops at the first error, with a non-zero exit code.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runtime = 'win-x64'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\YAT.App\YAT.App.csproj'
$documents = Join-Path $PSScriptRoot 'docs'

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

# ---- The version, from its one source ----

$version = (Invoke-Dotnet @('msbuild', $project, '-getProperty:Version', '-nologo') | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+') {
    throw "The version read from MSBuild is not a version: '$version'."
}

$name = "YAT-v$version-$runtime"
$artifacts = Join-Path $root 'artifacts'
$release = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
Write-Host "Publishing $name"

# Only this release's own output is replaced.
foreach ($path in @($release, $zip)) {
    if (-not $path.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace '$path': it is not under '$artifacts'."
    }

    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

# ---- Publish ----

$properties = @(
    '--configuration', 'Release',
    '--runtime', $runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:PublishReadyToRun=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '--nologo')

# Built from scratch, so the release never depends on what an earlier build left behind (an incremental build keeps
# assemblies compiled with debug information, and the path of their symbols, from a normal Release build).
$null = Invoke-Dotnet (@('build', $project, '--no-incremental') + $properties)
$null = Invoke-Dotnet (@('publish', $project, '--no-build', '--output', $release) + $properties)

# Debug symbols that packages ship alongside their native libraries are not part of the release.
Get-ChildItem -LiteralPath $release -Recurse -File -Filter '*.pdb' | Remove-Item -Force

# ---- Documents ----

Copy-Item -LiteralPath (Join-Path $root 'LICENSE.txt') -Destination $release
Copy-Item -LiteralPath (Join-Path $documents 'THIRD-PARTY-NOTICES.txt') -Destination $release
$utf8 = New-Object System.Text.UTF8Encoding($false)
$readme = [IO.File]::ReadAllText((Join-Path $documents 'README.txt'), $utf8).Replace('@VERSION@', $version)
[IO.File]::WriteAllText((Join-Path $release 'README.txt'), $readme, $utf8)

# The license and notice files the redistributed packages ship, unchanged, one folder per package - and every
# redistributed package must be named in THIRD-PARTY-NOTICES.txt.
$packages = ((Invoke-Dotnet @('nuget', 'locals', 'global-packages', '--list')) -join "`n") -replace '(?s)^.*?global-packages:\s*', ''
$packages = $packages.Trim()
if (-not (Test-Path -LiteralPath $packages)) {
    throw "The NuGet global packages folder '$packages' was not found."
}

$notices = [IO.File]::ReadAllText((Join-Path $documents 'THIRD-PARTY-NOTICES.txt'), $utf8)
$dependencies = (Get-Content -LiteralPath (Join-Path $release 'YAT.deps.json') -Raw | ConvertFrom-Json).libraries
$unlisted = @()
foreach ($library in $dependencies.PSObject.Properties) {
    if ($library.Value.type -notin @('package', 'runtimepack')) {
        continue
    }

    $id, $packageVersion = $library.Name -split '/'
    $id = $id -replace '^runtimepack\.', ''
    if ($notices -notmatch "(?m)(^|[\s,(\\])$([regex]::Escape($id))([\s,)\\]|$)") {
        $unlisted += "$id $packageVersion"
    }

    $folder = Join-Path $packages (Join-Path $id.ToLowerInvariant() $packageVersion)
    $files = @(Get-ChildItem -LiteralPath $folder -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|LICENCE|THIRD-PARTY-NOTICES|ThirdPartyNotices|NOTICE)' })
    if ($files.Count -gt 0) {
        $target = Join-Path $release (Join-Path 'licenses' $id)
        $null = New-Item -ItemType Directory -Path $target -Force
        $files | Copy-Item -Destination $target
    }
}

if ($unlisted.Count -gt 0) {
    throw "THIRD-PARTY-NOTICES.txt does not name these redistributed packages: $($unlisted -join ', ')."
}

# ---- Check what was published ----

foreach ($required in @('YAT.exe', 'YAT.dll', 'hostfxr.dll', 'coreclr.dll', 'libSkiaSharp.dll', 'libHarfBuzzSharp.dll', 'duckdb.dll', 'av_libglesv2.dll', 'LICENSE.txt', 'README.txt', 'THIRD-PARTY-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $release $required))) {
        throw "The release is missing $required."
    }
}

# A user's preferences live in their profile (%APPDATA%\YAT), never in a release: an unpacked or updated release must
# never carry, or overwrite, anyone's graph palettes (Task #050).
$preferences = @(Get-ChildItem -LiteralPath $release -Recurse -File |
    Where-Object { $_.Name -like 'graph-palettes*.json' -or $_.Name -like 'graph-palettes.json.*.tmp' })
if ($preferences.Count -gt 0) {
    throw "The release contains user preference files: $($preferences.Name -join ', ')."
}

$symbols = @(Get-ChildItem -LiteralPath $release -Recurse -File -Filter '*.pdb')
if ($symbols.Count -gt 0) {
    throw "The release still contains debug symbols: $($symbols.Name -join ', ')."
}

# ---- Zip ----

# The release folder itself, its files in path order, every entry named with '/' as the zip format requires (Windows
# PowerShell's Compress-Archive and ZipFile.CreateFromDirectory write '\').
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $release -Recurse -File |
        ForEach-Object { [pscustomobject]@{ File = $_.FullName; Entry = ($name + '/' + $_.FullName.Substring($release.Length + 1).Replace('\', '/')) } } |
        Sort-Object -Property Entry -CaseSensitive |
        ForEach-Object {
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.File, $_.Entry, [IO.Compression.CompressionLevel]::Optimal)
        }
}
finally {
    $archive.Dispose()
}

$files = @(Get-ChildItem -LiteralPath $release -Recurse -File)
$size = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("{0}: {1} files, {2:N1} MB" -f $release, $files.Count, ($size / 1MB))
Write-Host ("{0}: {1:N1} MB" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
