<#
.SYNOPSIS
    Builds the NutHub release archives on Windows (the same output as tools/package-release.sh).

.DESCRIPTION
    Publishes one self-contained, single-file executable per runtime and packs it in dist\ with the install
    scripts and service files:
      nuthub-<version>-win-x64.zip          nuthub.exe, install.ps1, LICENSE, README.md
      nuthub-<version>-linux-<arch>.tar.gz  nuthub, install.sh, nuthub.service, udev and polkit rules, LICENSE, README.md
    plus a .sha256 file per archive (sha256sum format). The version comes from Directory.Build.props.
    Linux archives keep the executable bits when run with PowerShell 7 or later (System.Formats.Tar); with
    Windows PowerShell 5.1 they are made with tar.exe and "sudo sh install.sh" is needed to run the installer.

.EXAMPLE
    pwsh tools/package-release.ps1 -Runtimes win-x64,linux-x64
#>
[CmdletBinding()]
param(
    [string[]] $Runtimes = @('win-x64', 'linux-x64', 'linux-arm64', 'linux-arm'),
    [string] $Output,
    [string] $ArtifactsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'dist' }
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $root 'artifacts\release' }
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path $Output).Path

[xml] $props = Get-Content -Raw (Join-Path $root 'Directory.Build.props')
$version = @($props.Project.PropertyGroup | Where-Object { $_['Version'] } | ForEach-Object { $_['Version'].InnerText })[0]
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
Write-Host "Packaging NutHub $version for: $($Runtimes -join ', ')"

$linuxFiles = @('install.sh', 'nuthub.service', '99-nuthub-ups.rules', '50-nuthub-poweroff.rules', '50-nuthub-poweroff.pkla')
$useTarApi = $PSVersionTable.PSVersion.Major -ge 7

function Copy-Docs([string] $destination) {
    foreach ($doc in @('LICENSE', 'README.md')) {
        $path = Join-Path $root $doc
        if (Test-Path $path) { Copy-Item $path $destination }
    }
}

# Copies a text file with LF line endings, whatever the checkout uses.
function Copy-Lf([string] $source, [string] $destination) {
    $text = [IO.File]::ReadAllText($source) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($destination, $text, (New-Object Text.UTF8Encoding($false)))
}

function New-TarGz([string] $archive, [string] $baseDir, [string] $folder) {
    if ($useTarApi) {
        $file = [IO.File]::Create($archive)
        try {
            $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionLevel]::Optimal)
            $writer = New-Object Formats.Tar.TarWriter($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
            try {
                $dirEntry = New-Object Formats.Tar.PaxTarEntry([Formats.Tar.TarEntryType]::Directory, "$folder/")
                $dirEntry.Mode = [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
                $writer.WriteEntry($dirEntry)
                foreach ($item in Get-ChildItem -File (Join-Path $baseDir $folder) | Sort-Object Name) {
                    $entry = New-Object Formats.Tar.PaxTarEntry([Formats.Tar.TarEntryType]::RegularFile, "$folder/$($item.Name)")
                    $executable = $item.Name -in @('nuthub', 'install.sh')
                    $entry.Mode = if ($executable) {
                        [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
                    } else {
                        [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
                    }
                    $entry.ModificationTime = $item.LastWriteTimeUtc
                    $stream = [IO.File]::OpenRead($item.FullName)
                    try {
                        $entry.DataStream = $stream
                        $writer.WriteEntry($entry)
                    }
                    finally { $stream.Dispose() }
                }
            }
            finally { $writer.Dispose(); $gzip.Dispose() }
        }
        finally { $file.Dispose() }
    }
    else {
        Write-Warning 'Windows PowerShell 5.1: the tar.gz archive does not keep executable bits (use PowerShell 7).'
        & tar.exe -C $baseDir -czf $archive $folder
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $archive" }
    }
}

$staging = Join-Path ([IO.Path]::GetTempPath()) ("nuthub-release-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    foreach ($rid in $Runtimes) {
        $name = "nuthub-$version-$rid"
        $publish = Join-Path $ArtifactsPath "publish\$rid"
        Write-Host "==> $rid"
        if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
        & dotnet publish (Join-Path $root 'src\NutHub\NutHub.csproj') -c Release -r $rid -o $publish `
            --artifacts-path $ArtifactsPath "-p:Version=$version" -p:DebugType=embedded -nologo -v:q
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

        $folder = Join-Path $staging $name
        New-Item -ItemType Directory -Path $folder | Out-Null
        Copy-Docs $folder
        if ($rid -like 'win-*') {
            Copy-Item (Join-Path $publish 'nuthub.exe') $folder
            Copy-Item (Join-Path $root 'packaging\windows\install.ps1') $folder
            $archive = Join-Path $Output "$name.zip"
            if (Test-Path $archive) { Remove-Item -Force $archive }
            Compress-Archive -Path $folder -DestinationPath $archive
        }
        else {
            Copy-Item (Join-Path $publish 'nuthub') $folder
            foreach ($file in $linuxFiles) {
                Copy-Lf (Join-Path $root "packaging\linux\$file") (Join-Path $folder $file)
            }
            $archive = Join-Path $Output "$name.tar.gz"
            if (Test-Path $archive) { Remove-Item -Force $archive }
            New-TarGz $archive $staging $name
        }

        $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
        $line = "$hash  $(Split-Path -Leaf $archive)`n"
        [IO.File]::WriteAllText("$archive.sha256", $line, (New-Object Text.UTF8Encoding($false)))
        Write-Host "    $archive"
    }
}
finally {
    Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
}

Write-Host "Done: $Output"
