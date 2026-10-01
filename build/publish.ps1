<#
.SYNOPSIS
    Publishes the YAT release for Windows x64 (Tasks #048 and #051).

.DESCRIPTION
    Builds src/YAT.App self-contained for win-x64 - not single-file, not trimmed, without ReadyToRun and without debug
    symbols - and lays the release out in the output folder (artifacts/ unless -OutputDirectory says otherwise):

        YAT-v<version>-win-x64/              YAT.exe, the .NET runtime, the managed and native dependencies,
                                             LICENSE.txt, README.txt, THIRD-PARTY-NOTICES.txt, licenses/ and
                                             yat-files.json - the inventory of every other file, with its size and SHA-256
        YAT-v<version>-win-x64.zip           that folder, zipped
        YAT-v<version>-win-x64.zip.sha256    the zip's SHA-256: "<sha256>  YAT-v<version>-win-x64.zip"
        yat-update.json                      the release manifest (version, the zip's URL, SHA-256 and size) - only with
                                             -PackageBaseUrl, the https address the zip will be published at

    <version> is the one in Directory.Build.props, asked of MSBuild, and must be a stable Major.Minor.Patch.
    README.txt and THIRD-PARTY-NOTICES.txt come from build/docs, LICENSE.txt from the repository root. The inventory,
    checksum and manifest are made, and the whole set is verified at the end, by build/YatRelease.cs - the release
    rules of YAT.Application and YAT.Infrastructure themselves. That release's own files in the output folder are
    replaced; nothing else is touched. Nothing is uploaded and no tag is made. Stops at the first error, with a non-zero
    exit code.

    -Official is for a release that will be published: the working tree must be clean, the tag v<version>, if it
    already exists, must be the commit being built, and -PackageBaseUrl is required. Without -Official the script is an
    ordinary build: no git checks, and the manifest only when a base URL is given.

.PARAMETER OutputDirectory
    Where the release is laid out; artifacts/ by default. Use another folder to try the release without touching the
    releases already in artifacts/.

.PARAMETER PackageBaseUrl
    The https address the zip will be downloadable under (the release's folder; the zip's name is added to it), for
    example https://github.com/<owner>/<repository>/releases/download/v0.2.0/. Without it no manifest is written.

.PARAMETER ReleaseNotesUrl
    The https address of the release notes, written into the manifest.

.PARAMETER Official
    The checks for a release that will be published (see above).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Official -PackageBaseUrl https://github.com/<owner>/<repository>/releases/download/v0.2.0/ -ReleaseNotesUrl https://github.com/<owner>/<repository>/releases/tag/v0.2.0
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$PackageBaseUrl,
    [string]$ReleaseNotesUrl,
    [switch]$Official)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runtime = 'win-x64'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\YAT.App\YAT.App.csproj'
$documents = Join-Path $PSScriptRoot 'docs'
$tool = Join-Path $PSScriptRoot 'YatRelease.cs'

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

# A step of build/YatRelease.cs; its output is shown.
function Invoke-Release {
    param([Parameter(Mandatory)][string[]]$Arguments)

    Invoke-Dotnet (@('run', '--file', $tool, '--') + $Arguments) | Write-Host
}

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments)

    $output = & git -C $root @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

# ---- The version, from its one source ----

$version = (Invoke-Dotnet @('msbuild', $project, '-getProperty:Version', '-nologo') | Where-Object { $_.Trim() } | Select-Object -Last 1).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+') {
    throw "The version read from MSBuild is not a version: '$version'."
}

# A stable Major.Minor.Patch, as the release rules read it - the version every name, manifest and tag is written from.
Invoke-Release @('version', $version)

# The URLs, before anything is built (the release rules check them again when the manifest is written).
foreach ($url in @($PackageBaseUrl, $ReleaseNotesUrl) | Where-Object { $_ }) {
    $parsed = $null
    if (-not [Uri]::TryCreate($url, [UriKind]::Absolute, [ref]$parsed) -or $parsed.Scheme -ne 'https') {
        throw "'$url' is not an https URL."
    }
}

if ($ReleaseNotesUrl -and -not $PackageBaseUrl) {
    throw '-ReleaseNotesUrl goes into the manifest, which needs -PackageBaseUrl.'
}

# ---- A release that will be published ----

if ($Official) {
    if (-not $PackageBaseUrl) {
        throw 'An official release needs -PackageBaseUrl: its manifest must say where the package is.'
    }

    $changes = @(Invoke-Git @('status', '--porcelain'))
    if ($changes.Count -gt 0) {
        throw "An official release is built from a clean working tree; it has changes:`n$($changes -join "`n")"
    }

    $tag = "v$version"
    $head = (Invoke-Git @('rev-parse', 'HEAD')).Trim()
    $tagged = @(& git -C $root rev-parse --verify --quiet "refs/tags/$tag^{commit}")
    if ($LASTEXITCODE -eq 0 -and $tagged.Count -gt 0 -and $tagged[0].Trim() -ne $head) {
        throw "The tag $tag already exists for another commit ($($tagged[0].Trim())); this is $head."
    }

    Write-Host "Official release $tag of $head"
}

$name = "YAT-v$version-$runtime"
$artifacts = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts' }
$null = New-Item -ItemType Directory -Path $artifacts -Force
$release = Join-Path $artifacts $name
$zip = Join-Path $artifacts "$name.zip"
$checksum = "$zip.sha256"
$manifest = Join-Path $artifacts 'yat-update.json'
Write-Host "Publishing $name to $artifacts"

# Only this release's own output is replaced.
foreach ($path in @($release, $zip, $checksum, $manifest)) {
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

# YAT.exe carries the one product version: its file version is <version>.0.
$fileVersion = (Get-Item -LiteralPath (Join-Path $release 'YAT.exe')).VersionInfo.FileVersion
if ($fileVersion -ne "$version.0") {
    throw "YAT.exe has the file version $fileVersion, not $version.0."
}

# ---- Inventory (Task #051) ----

# Every file of the release, with its size and SHA-256, in yat-files.json at the release's root - and so in the zip.
Invoke-Release @('inventory', $release, $version, $runtime)

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

# ---- Checksum and manifest (Task #051) ----

$package = @('package', $zip, $version, $runtime)
if ($PackageBaseUrl) {
    $package += @('--base-url', $PackageBaseUrl)
}

if ($ReleaseNotesUrl) {
    $package += @('--release-notes', $ReleaseNotesUrl)
}

Invoke-Release $package

# ---- The whole set, verified ----

# Names and versions agree, the checksum and the manifest are the zip's, and the zip holds exactly the files of its
# inventory - each the size and SHA-256 listed - and no user settings.
$verify = @('verify', $artifacts, $version, $runtime)
if ($Official) {
    $verify += '--require-manifest'
}

Invoke-Release $verify

$files = @(Get-ChildItem -LiteralPath $release -Recurse -File)
$size = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host ("{0}: {1} files, {2:N1} MB" -f $release, $files.Count, ($size / 1MB))
Write-Host ("{0}: {1:N1} MB" -f $zip, ((Get-Item -LiteralPath $zip).Length / 1MB))
