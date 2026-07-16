<#
.SYNOPSIS
  Build UUVR (Universal Unity VR) and optionally deploy to a BepInEx game folder.

.DESCRIPTION
  Configurations: legacy-mono | modern-mono | legacy-il2cpp | modern-il2cpp
  - legacy  = Unity 2019 or earlier
  - modern  = Unity 2020 or later
  - mono    = Managed / MonoBleedingEdge games
  - il2cpp  = GameAssembly.dll + il2cpp_data games

  Build output (Rai Pal style staging):
    build\mods\<Mono|Il2Cpp>\uuvr-<backend>-<generation>\plugins\
    build\mods\<Mono|Il2Cpp>\uuvr-<backend>-<generation>\patchers\

  Deploy copies plugins -> <Game>\BepInEx\plugins\
                   patchers -> <Game>\BepInEx\patchers\

.PARAMETER Configuration
  Build config. Use Auto with -GamePath to detect from the game.

.PARAMETER GamePath
  Game root (folder that contains the .exe and BepInEx). Enables deploy after build.

.PARAMETER BuildOnly
  Compile only; do not copy to game.

.PARAMETER DeployOnly
  Skip build; deploy existing artifacts for the chosen (or auto) configuration.

.PARAMETER All
  Build all configs that compile successfully (continues on per-config failure).

.PARAMETER SkipBuild
  Alias for DeployOnly.

.EXAMPLE
  .\scripts\Build-Deploy.ps1 -Configuration modern-mono

.EXAMPLE
  .\scripts\Build-Deploy.ps1 -GamePath "H:\Games\MyGame"

.EXAMPLE
  .\scripts\Build-Deploy.ps1 -Configuration modern-mono -GamePath "H:\Playnite1033\Games\tetiao\She Will Punish Them"
#>
[CmdletBinding()]
param(
    [ValidateSet("Auto", "legacy-mono", "modern-mono", "legacy-il2cpp", "modern-il2cpp")]
    [string]$Configuration = "Auto",

    [string]$GamePath = "",

    [switch]$BuildOnly,
    [switch]$DeployOnly,
    [switch]$SkipBuild,
    [switch]$All
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $RepoRoot "UnityUniversalVr.sln"))) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}
Set-Location $RepoRoot

$BuildRoot = Join-Path $RepoRoot "build"
$UuvrCsproj = Join-Path $RepoRoot "Uuvr\Uuvr.csproj"
$PatcherCsproj = Join-Path $RepoRoot "Uuvr.Patcher\Uuvr.Patcher.csproj"

function Write-Info([string]$msg) { Write-Host $msg -ForegroundColor Cyan }
function Write-Ok([string]$msg) { Write-Host $msg -ForegroundColor Green }
function Write-WarnLine([string]$msg) { Write-Host $msg -ForegroundColor Yellow }
function Write-Err([string]$msg) { Write-Host $msg -ForegroundColor Red }

function Get-UnityVersionFromGame([string]$gameDir) {
    $unityPlayer = Join-Path $gameDir "UnityPlayer.dll"
    if (Test-Path $unityPlayer) {
        $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($unityPlayer)
        if ($vi.FileVersion) {
            # e.g. 2020.1.17.8467405 -> use first three segments as major.minor.patch-ish
            $parts = $vi.ProductVersion
            if (-not $parts) { $parts = $vi.FileVersion }
            if ($parts -match '^(\d+)\.(\d+)') {
                return [pscustomobject]@{ Major = [int]$Matches[1]; Minor = [int]$Matches[2]; Raw = $parts }
            }
        }
    }

    $dataDirs = Get-ChildItem $gameDir -Directory -Filter "*_Data" -ErrorAction SilentlyContinue
    foreach ($d in $dataDirs) {
        $ggm = Join-Path $d.FullName "globalgamemanagers"
        if (-not (Test-Path $ggm)) { continue }
        $bytes = [System.IO.File]::ReadAllBytes($ggm)
        $len = [Math]::Min(2048, $bytes.Length)
        $text = [System.Text.Encoding]::ASCII.GetString($bytes, 0, $len)
        if ($text -match '(\d{4})\.(\d+)\.') {
            return [pscustomobject]@{ Major = [int]$Matches[1]; Minor = [int]$Matches[2]; Raw = $Matches[0].TrimEnd('.') }
        }
    }
    return $null
}

function Detect-GameProfile([string]$gameDir) {
    if (-not (Test-Path $gameDir)) {
        throw "Game path not found: $gameDir"
    }
    $gameDir = (Resolve-Path $gameDir).Path

    $isIl2Cpp = (Test-Path (Join-Path $gameDir "GameAssembly.dll")) -or
                (Test-Path (Join-Path $gameDir "il2cpp_data"))
    $isMono = (Test-Path (Join-Path $gameDir "MonoBleedingEdge")) -or
              (Test-Path (Join-Path $gameDir "Mono")) -or
              (@(Get-ChildItem $gameDir -Directory -Filter "*_Data" -ErrorAction SilentlyContinue |
                  ForEach-Object { Join-Path $_.FullName "Managed" } |
                  Where-Object { Test-Path $_ }).Count -gt 0)

    if ($isIl2Cpp) {
        $backend = "il2cpp"
    } elseif ($isMono) {
        $backend = "mono"
    } else {
        throw "Could not detect Mono vs IL2CPP for: $gameDir"
    }

    $uv = Get-UnityVersionFromGame $gameDir
    if ($null -eq $uv) {
        Write-WarnLine "Could not read Unity version; defaulting to modern (2020+)."
        $generation = "modern"
        $uvRaw = "unknown"
    } else {
        # Project rule: legacy = Unity 2019 or earlier; modern = 2020+
        if ($uv.Major -ge 2020) {
            $generation = "modern"
        } else {
            $generation = "legacy"
        }
        $uvRaw = $uv.Raw
    }

    $config = "$generation-$backend"
    return [pscustomobject]@{
        GamePath     = $gameDir
        Backend      = $backend
        Generation   = $generation
        Configuration = $config
        UnityVersion = $uvRaw
        HasBepInEx   = Test-Path (Join-Path $gameDir "BepInEx")
    }
}

function Get-ModStagingDir([string]$config) {
    switch ($config) {
        "legacy-mono"   { return Join-Path $BuildRoot "mods\Mono\uuvr-mono-legacy" }
        "modern-mono"   { return Join-Path $BuildRoot "mods\Mono\uuvr-mono-modern" }
        "legacy-il2cpp" { return Join-Path $BuildRoot "mods\Il2Cpp\uuvr-il2cpp-legacy" }
        "modern-il2cpp" { return Join-Path $BuildRoot "mods\Il2Cpp\uuvr-il2cpp-modern" }
        default { throw "Unknown configuration: $config" }
    }
}

function Invoke-UuvrBuild([string]$config) {
    Write-Info "=== Building configuration: $config ==="
    New-Item -ItemType Directory -Force -Path $BuildRoot | Out-Null

    $common = @(
        "-p:BepInExDir=$BuildRoot",
        "-v", "minimal",
        "--nologo"
    )

    & dotnet build $UuvrCsproj -c $config @common
    if ($LASTEXITCODE -ne 0) {
        throw "Uuvr plugin build failed for $config (exit $LASTEXITCODE)"
    }

    & dotnet build $PatcherCsproj -c $config @common
    if ($LASTEXITCODE -ne 0) {
        throw "Uuvr.Patcher build failed for $config (exit $LASTEXITCODE)"
    }

    $stage = Get-ModStagingDir $config
    $pluginDll = Join-Path $stage "plugins\Uuvr.dll"
    $patcherDll = Join-Path $stage "patchers\Uuvr.Patcher.dll"
    if (-not (Test-Path $pluginDll)) {
        throw "Missing plugin output: $pluginDll"
    }
    if (-not (Test-Path $patcherDll)) {
        throw "Missing patcher output: $patcherDll"
    }
    Write-Ok "Build OK: $stage"
    return $stage
}

function Deploy-Uuvr([string]$config, [string]$gameDir) {
    $stage = Get-ModStagingDir $config
    $pluginsSrc = Join-Path $stage "plugins"
    $patchersSrc = Join-Path $stage "patchers"

    if (-not (Test-Path (Join-Path $pluginsSrc "Uuvr.dll"))) {
        throw "No build artifacts for $config at $pluginsSrc. Run build first."
    }

    $bepinex = Join-Path $gameDir "BepInEx"
    if (-not (Test-Path $bepinex)) {
        throw "BepInEx not found under game. Install BepInEx first: $bepinex"
    }

    $pluginsDst = Join-Path $bepinex "plugins"
    $patchersDst = Join-Path $bepinex "patchers"
    New-Item -ItemType Directory -Force -Path $pluginsDst | Out-Null
    New-Item -ItemType Directory -Force -Path $patchersDst | Out-Null

    # Prefer a dedicated subfolder so UUVR is easy to remove / update
    $pluginsTarget = Join-Path $pluginsDst "UUVR"
    $patchersTarget = Join-Path $patchersDst "UUVR"
    New-Item -ItemType Directory -Force -Path $pluginsTarget | Out-Null
    New-Item -ItemType Directory -Force -Path $patchersTarget | Out-Null

    Write-Info "Deploy plugins: $pluginsSrc -> $pluginsTarget"
    Copy-Item -Path (Join-Path $pluginsSrc "*") -Destination $pluginsTarget -Recurse -Force

    Write-Info "Deploy patchers: $patchersSrc -> $patchersTarget"
    Copy-Item -Path (Join-Path $patchersSrc "*") -Destination $patchersTarget -Recurse -Force

    # Remove stale flat copies if previously deployed to plugins root
    foreach ($name in @("Uuvr.dll", "Uuvr.XR.Management.dll", "Uuvr.XR.OpenVR.dll", "Uuvr.XR.OpenXR.dll")) {
        $flat = Join-Path $pluginsDst $name
        if (Test-Path $flat) {
            Remove-Item $flat -Force -ErrorAction SilentlyContinue
            Write-WarnLine "Removed old flat plugin copy: $flat"
        }
    }
    $flatPatcher = Join-Path $patchersDst "Uuvr.Patcher.dll"
    if (Test-Path $flatPatcher) {
        Remove-Item $flatPatcher -Force -ErrorAction SilentlyContinue
        Write-WarnLine "Removed old flat patcher copy: $flatPatcher"
    }

    Write-Ok "Deployed $config to:"
    Write-Host "  $pluginsTarget"
    Write-Host "  $patchersTarget"
}

# ---- main ----
$doBuild = -not ($DeployOnly -or $SkipBuild)
$doDeploy = -not $BuildOnly

if ($All) {
    $configs = @("legacy-mono", "modern-mono", "legacy-il2cpp", "modern-il2cpp")
    $failed = @()
    foreach ($c in $configs) {
        try {
            if ($doBuild) { Invoke-UuvrBuild $c | Out-Null }
        } catch {
            Write-Err $_
            $failed += $c
        }
    }
    if ($failed.Count -gt 0) {
        Write-WarnLine "Failed configs: $($failed -join ', ')"
        # mono configs are required for this repo's primary path
        if ($failed -contains "legacy-mono" -or $failed -contains "modern-mono") {
            exit 1
        }
    }
    if ($doDeploy -and $GamePath) {
        $profile = Detect-GameProfile $GamePath
        Write-Info "Auto profile: $($profile.Configuration) (Unity $($profile.UnityVersion), $($profile.Backend))"
        Deploy-Uuvr $profile.Configuration $profile.GamePath
    }
    Write-Ok "Done."
    exit 0
}

if ($Configuration -eq "Auto") {
    if (-not $GamePath) {
        Write-Err "Configuration is Auto: provide -GamePath or set -Configuration explicitly."
        Write-Host "Examples:"
        Write-Host '  .\scripts\Build-Deploy.ps1 -Configuration modern-mono'
        Write-Host '  .\scripts\Build-Deploy.ps1 -GamePath "H:\path\to\game"'
        exit 1
    }
    $profile = Detect-GameProfile $GamePath
    $Configuration = $profile.Configuration
    Write-Info "Detected: Unity $($profile.UnityVersion) / $($profile.Backend) -> $Configuration"
    if (-not $profile.HasBepInEx -and $doDeploy) {
        throw "Game has no BepInEx folder. Install BepInEx before deploying."
    }
} elseif ($GamePath) {
    $profile = Detect-GameProfile $GamePath
    Write-Info "Game: Unity $($profile.UnityVersion) / $($profile.Backend) (deploy config override: $Configuration)"
}

if ($doBuild) {
    Invoke-UuvrBuild $Configuration | Out-Null
}

if ($doDeploy) {
    if (-not $GamePath) {
        Write-WarnLine "No -GamePath; build finished without deploy."
        Write-Host "Staging: $(Get-ModStagingDir $Configuration)"
        exit 0
    }
    Deploy-Uuvr $Configuration ((Resolve-Path $GamePath).Path)
}

Write-Ok "Done."
