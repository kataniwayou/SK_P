#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    Phase 28 (IDENT-02 / Pitfall 1) — the HIGHEST-RISK cross-OS SourceHash reproducibility gate.

.DESCRIPTION
    Proves the SourceHash embedded by the WINDOWS host SDK build EQUALS the SourceHash embedded by
    the LINUX Docker build of Processor.Sample. This is load-bearing: Plan 03's real-stack E2E
    reflects the host-built hash and registers it as the Processor DB row, but the live container
    runs the Linux-Docker-built hash. If they diverge, identity never resolves, the container never
    goes Healthy, and the liveness-gated Start fails silently (the container log loops
    "Processor row not yet registered for hash"). This script catches that divergence before the
    E2E depends on it.

    Both hashes are obtained by REFLECTING the genuine [assembly: AssemblyMetadata("SourceHash", ...)]
    attribute off the built Processor.Sample.dll — the same value the runtime reader
    (AssemblyMetadataSourceHashProvider) reads — NOT by recomputing the algorithm (D-08). The
    attribute blob is decoded directly via System.Reflection.Metadata's PE reader so the script has
    no extra-assembly dependency and works identically against the host dll and the dll extracted
    from the Linux image.

.OUTPUTS
    Prints "HOST  SourceHash = <64-hex>", "DOCKER SourceHash = <64-hex>", and "MATCH" or "DIVERGED".
    Exit 0 when the two hashes are byte-equal; exit 1 on divergence or build failure.
#>
[CmdletBinding()]
param(
    [string]$ImageTag = "processor-sample:hashcheck"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Repo root = two levels up from this script (scripts/ -> repo root).
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$DockerfilePath = Join-Path $RepoRoot "src/Processor.Sample/Dockerfile"

# ---------------------------------------------------------------------------
# Reflect the embedded SourceHash off a built Processor.Sample.dll.
# Decodes the AssemblyMetadataAttribute custom-attribute blob directly: prolog (0x0001) followed by
# two length-prefixed UTF-8 SerStrings (the key "SourceHash" and the 64-hex value). This is exactly
# the attribute the runtime reader consumes, so the script proves the embed, not the algorithm.
# ---------------------------------------------------------------------------
function Read-SerString {
    param([byte[]]$Bytes, [ref]$Pos)
    $p = $Pos.Value
    if ($Bytes[$p] -eq 0xFF) { $Pos.Value = $p + 1; return $null }   # null marker
    $b0 = $Bytes[$p]
    if (($b0 -band 0x80) -eq 0) {
        $len = $b0; $p += 1
    } elseif (($b0 -band 0xC0) -eq 0x80) {
        $len = (($b0 -band 0x3F) -shl 8) -bor $Bytes[$p + 1]; $p += 2
    } else {
        $len = (($b0 -band 0x1F) -shl 24) -bor ($Bytes[$p + 1] -shl 16) -bor ($Bytes[$p + 2] -shl 8) -bor $Bytes[$p + 3]; $p += 4
    }
    $s = [System.Text.Encoding]::UTF8.GetString($Bytes, $p, $len)
    $Pos.Value = $p + $len
    return $s
}

function Get-EmbeddedSourceHash {
    param([Parameter(Mandatory)] [string]$DllPath)
    if (-not (Test-Path $DllPath)) { throw "Assembly not found: $DllPath" }
    $fs = [System.IO.File]::OpenRead($DllPath)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($fs)
        $mr = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        foreach ($h in $mr.CustomAttributes) {
            $ca = $mr.GetCustomAttribute($h)
            $ctor = $ca.Constructor
            if ($ctor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
            $mref = $mr.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$ctor)
            $parent = $mref.Parent
            if ($parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
            $tref = $mr.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$parent)
            if ($mr.GetString($tref.Name) -ne "AssemblyMetadataAttribute") { continue }
            $blob = $mr.GetBlobBytes($ca.Value)
            $pos = 2                                   # skip the 0x0001 prolog
            $posRef = [ref]$pos
            $key = Read-SerString -Bytes $blob -Pos $posRef
            $val = Read-SerString -Bytes $blob -Pos $posRef
            if ($key -eq "SourceHash") { return $val }
        }
    } finally {
        $fs.Dispose()
    }
    throw "No [assembly: AssemblyMetadata('SourceHash', ...)] attribute found on $DllPath"
}

# ===========================================================================
# (1) HOST build — Windows SDK publish, then reflect.
# ===========================================================================
Write-Host "==> HOST build: dotnet publish src/Processor.Sample -c Release" -ForegroundColor Cyan
$hostPublishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ph-host-" + [Guid]::NewGuid().ToString("N"))
& dotnet publish (Join-Path $RepoRoot "src/Processor.Sample/Processor.Sample.csproj") `
    -c Release -o $hostPublishDir --nologo /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { Write-Host "HOST build FAILED" -ForegroundColor Red; exit 1 }

$hostDll = Join-Path $hostPublishDir "Processor.Sample.dll"
$hostHash = Get-EmbeddedSourceHash -DllPath $hostDll

# ===========================================================================
# (2) DOCKER build — Linux image, extract the published dll, then reflect.
# ===========================================================================
Write-Host "==> DOCKER build: docker build -f $DockerfilePath -t $ImageTag ." -ForegroundColor Cyan
& docker build -f $DockerfilePath -t $ImageTag $RepoRoot
if ($LASTEXITCODE -ne 0) { Write-Host "DOCKER build FAILED" -ForegroundColor Red; exit 1 }

# Extract /app/Processor.Sample.dll from the runtime layer via a stopped container (no exec needed —
# the aspnet image is reflected on the HOST so the decode path is byte-identical to the host build).
$dockerDll = Join-Path ([System.IO.Path]::GetTempPath()) ("ph-docker-" + [Guid]::NewGuid().ToString("N") + ".dll")
$cid = (& docker create $ImageTag).Trim()
try {
    & docker cp "${cid}:/app/Processor.Sample.dll" $dockerDll
    if ($LASTEXITCODE -ne 0) { Write-Host "docker cp FAILED" -ForegroundColor Red; exit 1 }
} finally {
    & docker rm -f $cid | Out-Null
}
$dockerHash = Get-EmbeddedSourceHash -DllPath $dockerDll

# ===========================================================================
# (3) Compare.
# ===========================================================================
Write-Host ""
Write-Host "HOST   SourceHash = $hostHash"
Write-Host "DOCKER SourceHash = $dockerHash"
Write-Host ""

# Cleanup temp artifacts (best-effort).
try { Remove-Item -Recurse -Force $hostPublishDir -ErrorAction SilentlyContinue } catch { }
try { Remove-Item -Force $dockerDll -ErrorAction SilentlyContinue } catch { }

if ($hostHash -eq $dockerHash) {
    Write-Host "MATCH — cross-OS SourceHash is reproducible (host == docker)." -ForegroundColor Green
    exit 0
} else {
    Write-Host "DIVERGED — host and docker SourceHash differ." -ForegroundColor Red
    Write-Host "Remediation (Pitfall 1, SourceHash.targets): confirm forward-slash path normalization" -ForegroundColor Yellow
    Write-Host "(Replace('\\','/') + Ordinal sort), LF content normalization (\\r\\n,\\r -> \\n), and the" -ForegroundColor Yellow
    Write-Host "deterministic glob excludes (obj/bin/*.g.cs/GlobalUsings/AssemblyInfo). A leaked CRLF or" -ForegroundColor Yellow
    Write-Host "back-slash path is the usual cause of a Windows-vs-Linux divergence." -ForegroundColor Yellow
    exit 1
}
