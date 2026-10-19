param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    # Build into another folder than ./dist (which is wiped first), for example while a server from dist is running.
    [string]$Out = ""
)

# Builds everything into ./dist:
#   dist/WKRace-<version>.zip          Thunderstore package: the mod (WKSR is inside it, from 0.9.3)
#   dist/WKRace-Server-<version>.zip   for hosts: a self-contained server exe and a start script
#   dist/wkrace-server-linux-x64-<version>.tar.gz   the server for the Linux VPS (extract into /opt/wkrace)
#   dist/WKRace.dll, dist/server/, dist/devtools/   the unpacked builds
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = if ($Out) { $Out } else { Join-Path $root "dist" }
$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version

# Plugin.cs states the version to BepInEx; it must agree with Directory.Build.props.
$pluginSource = Get-Content (Join-Path $root "src\WKRace.Mod\Plugin.cs") -Raw
foreach ($v in @($version)) {
    if ($pluginSource -notmatch [regex]::Escape("Version = `"$v`"")) { throw "Plugin.cs does not say version $v (Directory.Build.props does): update both together." }
}

function Run($what, [scriptblock]$cmd) {
    Write-Host "== $what" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$what failed ($LASTEXITCODE)" }
}

function Zip($folder, $zipPath) {
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $folder "*") -DestinationPath $zipPath
}

if (Test-Path $dist) {
    # Files first: an empty folder left behind is harmless. A running exe still in dist stops the build here.
    Get-ChildItem $dist -Recurse -File | Remove-Item -Force
    Get-ChildItem $dist -Directory | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null

# The two mods the plugin compiles against (ImuiBepInEx, WKLib) are downloaded, not kept in the repository.
& (Join-Path $PSScriptRoot "fetch-libs.ps1")
if (-not $?) { throw "fetching lib/ failed" }

if (-not $SkipTests) {
    Run "tests" { dotnet test (Join-Path $root "tests\WKRace.Tests\WKRace.Tests.csproj") -c $Configuration }
}

# PluginInstallDir= stops the build from copying into a local game profile.
Run "plugin" { dotnet build (Join-Path $root "src\WKRace.Mod\WKRace.Mod.csproj") -c $Configuration -p:PluginInstallDir= }
Copy-Item (Join-Path $root "src\WKRace.Mod\bin\$Configuration\netstandard2.1\WKRace.dll") $dist

# Self-contained single files: neither players nor hosts need .NET installed.
$single = @("-r", "win-x64", "--self-contained", "true", "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true")
Run "server" { dotnet publish (Join-Path $root "src\WKRace.Server\WKRace.Server.csproj") -c $Configuration @single -o (Join-Path $dist "server") }
# The VPS build: a self-contained folder (not single-file), extracted into /opt/wkrace on the server.
$linux = Join-Path $dist "server-linux-x64"
Run "server (linux-x64)" { dotnet publish (Join-Path $root "src\WKRace.Server\WKRace.Server.csproj") -c $Configuration -r linux-x64 --self-contained true -o $linux }
# The VPS deploy kit (deploy.sh, backup script and timer) travels inside the tarball, with Unix line endings.
$deploy = Join-Path $linux "deploy"
New-Item -ItemType Directory -Path $deploy | Out-Null
Get-ChildItem (Join-Path $root "tools\vps") -File | ForEach-Object {
    $text = [IO.File]::ReadAllText($_.FullName) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $deploy $_.Name), $text, (New-Object Text.UTF8Encoding $false))
}
# Windows' own tar (bsdtar): a Git for Windows tar earlier on PATH would read "C:" as a remote host.
Run "server tarball" { & (Join-Path $env:SystemRoot "System32\tar.exe") -czf (Join-Path $dist "wkrace-server-linux-x64-$version.tar.gz") -C $linux . }
Run "devtools" { dotnet publish (Join-Path $root "src\WKRace.DevTools\WKRace.DevTools.csproj") -c $Configuration -o (Join-Path $dist "devtools") }
Get-ChildItem $dist -Recurse -Filter *.pdb | Remove-Item

# ---- Thunderstore package: manifest + icon + readme + changelog + plugin -----------------------
# Published by the team WKSR as "WKRace". From 0.9.3 WKSR is part of the mod (a button on the game's main menu),
# so the package is the plugin alone.
$pkg = Join-Path $dist "package"
New-Item -ItemType Directory -Path $pkg | Out-Null
Copy-Item (Join-Path $dist "WKRace.dll") $pkg
Copy-Item (Join-Path $root "tools\package\THUNDERSTORE-README.md") (Join-Path $pkg "README.md")
Copy-Item (Join-Path $root "tools\package\CHANGELOG.md") $pkg
@{
    name           = "WKRace"
    version_number = $version
    website_url    = ""
    description    = "Speedrun matches up the same seeded White Knuckle climb: live splits, ghosts, ranked 1v1 and solo, leaderboards, party matches of up to 15 and chat. In the game's main menu."
    # ImuiBepInEx and WKLib draw the in-game WKSR menu; the mod manager installs them with this package.
    dependencies   = @("BepInEx-BepInExPack-5.4.2305", "prideunique-ImuiBepInEx-1.0.1", "MonkSilly-WKLib-0.3.0")
} | ConvertTo-Json | ForEach-Object {
    # Without a byte-order mark (Set-Content -Encoding UTF8 in Windows PowerShell writes one; Thunderstore's validator wants plain JSON).
    [IO.File]::WriteAllText((Join-Path $pkg "manifest.json"), $_, (New-Object Text.UTF8Encoding $false))
}
# Thunderstore wants a 256x256 PNG icon: the logo.
Copy-Item (Join-Path $root "tools\package\icon.png") $pkg
$modZip = Join-Path $dist "WKRace-$version.zip"
Zip $pkg $modZip
Remove-Item $pkg -Recurse -Force

# ---- Host package --------------------------------------------------------------------------
$host_ = Join-Path $dist "host"
New-Item -ItemType Directory -Path $host_ | Out-Null
Copy-Item (Join-Path $dist "server\WKRace.Server.exe") $host_
# The template may hold the developer's own address; the packaged copy always starts empty.
$bat = (Get-Content (Join-Path $root "tools\package\start-server.bat") -Raw) -replace '(?m)^set PUBLIC_ADDRESS=.*$', 'set PUBLIC_ADDRESS='
[IO.File]::WriteAllText((Join-Path $host_ "start-server.bat"), ($bat -replace "`r?`n", "`r`n"), [Text.Encoding]::ASCII)
(Get-Content (Join-Path $root "tools\package\SERVER-README.txt") -Raw).Replace("{VERSION}", $version) |
    Set-Content (Join-Path $host_ "README.txt") -Encoding UTF8
Zip $host_ (Join-Path $dist "WKRace-Server-$version.zip")
Remove-Item $host_ -Recurse -Force

Write-Host "Done: $dist" -ForegroundColor Green
Get-ChildItem $dist -File | Where-Object { $_.Extension -in ".zip", ".gz" } | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
