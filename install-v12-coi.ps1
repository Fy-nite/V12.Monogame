<#
.SYNOPSIS
Installs the V12.Bindings .coi package into the Contract LSP project roots.

.DESCRIPTION
The Contract language server discovers [ClassBinding] modules (V12.World,
V12.Components, V12.Log, ...) from .coi packages extracted under
<projectRoot>/.purr/packages/, where <projectRoot> is marked by a
contract.ctproj file. This script builds that package layout from the
V12.Monogame.GL build output:

  1. Writes contract.ctproj markers (when missing) so the LSP finds the roots.
  2. Recreates .purr/packages/v12.bindings/ with manifest.json, the
     dependency-closure DLLs under bindings/, and — when ccl is on PATH —
     a generated facade module under lib/ (ccl bindgen over V12.dll, which
     gives the editor real engine-type completion on top of the ClassBinding
     module names).

Idempotent: safe to re-run after every build. The package is gitignored;
re-run this script on a fresh clone.

.EXAMPLE
./install-v12-coi.ps1

.EXAMPLE
./install-v12-coi.ps1 -BuildOutput libs/V12.Monogame/V12.Monogame.DX/bin/Debug/net10.0-windows
#>
[CmdletBinding()]
param(
    [string]$BuildOutput = "libs/V12.Monogame/V12.Monogame.GL/bin/Debug/net10.0",
    [string[]]$ProjectRoots = @("games_src/SpinWorld", ".")
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $repoRoot

$buildOutFull = Join-Path $repoRoot $BuildOutput
if (-not (Test-Path $buildOutFull)) {
    Write-Error "Build output not found: $buildOutFull`nBuild V12.Monogame.GL first: dotnet build libs/V12.Monogame/V12.Monogame.GL"
}
$bindingsDll = Join-Path $buildOutFull "V12.Bindings.dll"
if (-not (Test-Path $bindingsDll)) {
    Write-Error "V12.Bindings.dll not found in '$buildOutFull'. Build V12.Monogame.GL first."
}

$engineDll = Join-Path $buildOutFull "V12.dll"
$ccl = Get-Command ccl -ErrorAction SilentlyContinue

foreach ($root in $ProjectRoots) {
    $rootPath = if ($root -eq ".") { $repoRoot } else { Join-Path $repoRoot $root }
    if (-not (Test-Path $rootPath)) {
        Write-Warning "Project root '$rootPath' does not exist - skipping."
        continue
    }

    Write-Host "==> $root"

    # 1. contract.ctproj marker: the LSP walks up from open files to this
    #    file, then reads .purr/packages/ next to it.
    $ctproj = Join-Path $rootPath "contract.ctproj"
    if (-not (Test-Path $ctproj)) {
        $isRepoRoot = (Resolve-Path $rootPath).Path -eq (Resolve-Path $repoRoot).Path
        $marker = if ($isRepoRoot) {
            @{ Name = "V12"; Type = "lib"; Main = "scripts/SpinElement.ct" }
        } else {
            @{ Name = (Split-Path $rootPath -Leaf); Type = "lib"; Main = "scripts/Main.ct" }
        }
        $marker | ConvertTo-Json | Set-Content -Path $ctproj -Encoding UTF8
        Write-Host "  created $ctproj"
    } else {
        Write-Host "  exists  $ctproj"
    }

    # 2. Extracted .coi package layout.
    $pkgDir = Join-Path $rootPath ".purr/packages/v12.bindings"
    $libDir = Join-Path $pkgDir "lib"
    $bindDir = Join-Path $pkgDir "bindings"
    Remove-Item $pkgDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $bindDir -Force | Out-Null
    New-Item -ItemType Directory -Path $libDir -Force | Out-Null

    # The full managed closure ships next to the listed binding assembly so
    # Assembly.LoadFrom dependency probing resolves V12.dll, Contract.*,
    # ObjektRT.* and friends inside the LSP process. Locked files (e.g. by a
    # running LSP/editor) are skipped with a warning instead of failing.
    $locked = 0
    foreach ($dll in (Get-ChildItem $buildOutFull -Filter *.dll)) {
        $dest = Join-Path $bindDir $dll.Name
        $copied = $false
        for ($attempt = 1; $attempt -le 3 -and -not $copied; $attempt++) {
            try {
                Copy-Item $dll.FullName $dest -Force -ErrorAction Stop
                $copied = $true
            }
            catch {
                if ($attempt -eq 3) { $locked++; Write-Warning "  skip locked: $($dll.Name)" }
                else { Start-Sleep -Milliseconds 200 }
            }
        }
    }
    Write-Host "  package $pkgDir ($((Get-ChildItem $bindDir -Filter *.dll).Count) dlls$(if ($locked) { ", $locked locked skipped" }))"

    $modules = @()
    $namespaces = $null

    # 3. Bindgen: one facade .ct per namespace over the engine assembly,
    #    compiled to one .orbt module per namespace (requires ccl). Emitted
    #    into lib/ directly — no .coi round-trip, so no native-runtime payload
    #    lands in the temp dir. The manifest namespace map covers every
    #    declared namespace plus its ancestor prefixes, aggregating descendant
    #    modules (so `import V12.Core;` loads the whole subtree).
    if ($ccl -and (Test-Path $engineDll)) {
        & ccl bindgen $engineDll -o $libDir --bind $bindingsDll --emit-path ../bindings/V12.dll
        if ($LASTEXITCODE -eq 0) {
            $nsMap = @{}
            $ok = $true
            foreach ($ct in (Get-ChildItem $libDir -Filter *.ct | Sort-Object Name)) {
                $orbt = [System.IO.Path]::ChangeExtension($ct.FullName, '.orbt')
                $compileOut = & ccl -c $ct.FullName -o $orbt 2>&1
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path $orbt)) {
                    Write-Warning "Facade module compile failed for $($ct.Name) - installing bindings-only package."
                    $compileOut | Select-Object -Last 15 | ForEach-Object { Write-Warning "  $_" }
                    $ok = $false
                    break
                }

                $firstNs = $null
                foreach ($line in Get-Content $ct.FullName) {
                    $trimmed = $line.TrimStart()
                    if ($trimmed -match '^namespace\s+([A-Za-z0-9_.]+)\s*;') { $firstNs = $Matches[1]; break }
                    if ($trimmed -ne '' -and -not $trimmed.StartsWith('//')) { break }
                }

                $modName = 'lib/' + [System.IO.Path]::GetFileName($orbt)
                $modules += $modName
                if ($firstNs) {
                    $parts = $firstNs -split '\.'
                    for ($i = 1; $i -le $parts.Count; $i++) {
                        $prefix = $parts[0..($i - 1)] -join '.'
                        if (-not $nsMap.ContainsKey($prefix)) { $nsMap[$prefix] = @() }
                        if ($nsMap[$prefix] -notcontains $modName) { $nsMap[$prefix] += $modName }
                    }
                }
                Write-Host "  facade  $modName$(if ($firstNs) { "  ($firstNs)" })"
            }
            if ($ok) {
                foreach ($key in @($nsMap.Keys)) { $nsMap[$key] = @($nsMap[$key] | Sort-Object) }
                $namespaces = $nsMap
            }
            else {
                $modules = @()
            }
        }
        else {
            Write-Warning "ccl bindgen failed - installing bindings-only package."
        }
    }
    else {
        Write-Warning "ccl not on PATH (or V12.dll missing) - installing bindings-only package."
    }

    $manifest = [ordered]@{
        Name       = "v12.bindings"
        Version    = "1.0.0"
        Type       = "lib"
        Modules    = $modules
        Namespaces = $namespaces
        Bindings   = @("bindings/V12.Bindings.dll")
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $pkgDir "manifest.json") -Encoding UTF8
}

Write-Host ""
Write-Host "Done. Restart the Contract language server (or reload the VS Code window)"
Write-Host "so it re-scans .purr/packages/. Open games_src/SpinWorld/scripts/Main.ct -"
Write-Host "V12.World / V12.Components / V12.Log should now resolve."
