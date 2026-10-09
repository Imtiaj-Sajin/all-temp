# Builds the release files into .\dist:
#   AllTemp-Setup-<version>.exe     installer: optional PawnIO driver, start with Windows, and the
#                                   .NET 8 Desktop Runtime downloaded from Microsoft only if missing
#   AllTemp-<version>-portable.exe  just the exe (needs the .NET 8 Desktop Runtime)
# -Dist picks another output folder, e.g. when a running copy still locks a file in .\dist.
param([string]$Iscc = "", [string]$Dist = "dist")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

[xml]$proj = Get-Content AllTemp.csproj
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version

if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }
}

Remove-Item -Recurse -Force $Dist, out -ErrorAction SilentlyContinue

dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o out
if ($LASTEXITCODE) { throw "dotnet publish failed" }

New-Item -ItemType Directory -Force $Dist | Out-Null
try { Copy-Item out\AllTemp.exe "$Dist\AllTemp-$version-portable.exe" -ErrorAction Stop }
catch { throw "Can't write to $Dist (is AllTemp running from there? exit it, or pass -Dist somewhere-else): $_" }

$pawnIo = Get-ChildItem obj -Recurse -Filter PawnIO_setup.exe | Select-Object -First 1
& $Iscc /Qp "/DAppVersion=$version" "/DAppExe=$PSScriptRoot\out\AllTemp.exe" "/DPawnIoSetup=$($pawnIo.FullName)" "/O$PSScriptRoot\$Dist" installer\AllTemp.iss
if ($LASTEXITCODE) { throw "Inno Setup failed" }

Get-ChildItem $Dist | ForEach-Object { "{0,-34} {1,5:N1} MB  sha256 {2}" -f $_.Name, ($_.Length / 1MB), (Get-FileHash $_.FullName).Hash }
