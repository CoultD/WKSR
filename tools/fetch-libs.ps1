# Downloads the two mods the in-game menu is drawn with from Thunderstore into lib/ (git-ignored): the plugin compiles
# against them, and the mod manager installs them for players as dependencies. They are other people's work, so they
# are not kept in this repository. The versions are the ones the package's manifest depends on (tools/build.ps1).
param([switch]$Force)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$lib = Join-Path $root "lib"
New-Item -ItemType Directory -Force $lib | Out-Null

$packages = @(
    @{ Namespace = "prideunique"; Name = "ImuiBepInEx"; Version = "1.0.1"; Dll = "ImuiBepInEx.dll" },
    @{ Namespace = "MonkSilly"; Name = "WKLib"; Version = "0.3.0"; Dll = "WKLib.dll" }
)
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($p in $packages) {
    $target = Join-Path $lib $p.Dll
    if ((Test-Path $target) -and -not $Force) { continue }
    $url = "https://thunderstore.io/package/download/$($p.Namespace)/$($p.Name)/$($p.Version)/"
    $zip = Join-Path ([IO.Path]::GetTempPath()) ("wkrace-" + $p.Name + "-" + $p.Version + ".zip")
    Write-Host "Downloading $($p.Namespace)-$($p.Name)-$($p.Version) from Thunderstore..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entry = $archive.Entries | Where-Object { $_.Name -eq $p.Dll } | Select-Object -First 1
        if ($entry -eq $null) { throw "$($p.Dll) is not in $url" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
    }
    finally {
        $archive.Dispose()
        Remove-Item $zip -Force
    }
    Write-Host "  -> lib\$($p.Dll)"
}
