# Downloads the tools VFlasher needs to flash boards over USB in Qualcomm EDL
# mode, checks their SHA-256, and puts them next to vflasher.exe:
#
#   <Dest>\qdl\qdl.exe, LICENSE     qdl v2.4 (linux-msm/qdl, BSD-3-Clause), Windows build
#                                   from arduino/qdl-packing, the one Arduino's own flasher uses
#   <Dest>\drivers\qcserlib.inf/.cat  WinUSB driver for USB 05C6:9008, signed by Qualcomm
#                                   (BSD-3-Clause), from arduino/arduino-flasher-cli
#
# Usage: pwsh windows/installer/fetch-edl-tools.ps1 -Dest windows/publish/win-x64
param([Parameter(Mandatory = $true)][string]$Dest)
$ErrorActionPreference = 'Stop'

$QdlUrl = 'https://github.com/arduino/qdl-packing/releases/download/v2.4-26/qdl_v2.4-26_Windows_32bit.tar.gz'
$QdlSha = '2535528aa7fd64bf1d6805d64f0d94797eb66613ca9f10825d19b5913e730b29'
$DriverBase = 'https://raw.githubusercontent.com/arduino/arduino-flasher-cli/9b009410fcfcca217cdfe4ae84a039c1baa58138/cmd/arduino-flasher-cli/drivers/src'
$DriverFiles = @{
    'qcserlib.inf' = 'd154a09c4f872848106c4a670a1f95e23aed72c599718672a00cfb3be7257a85'
    'qcserlib.cat' = '741ac1f63f4943c490a9c69ace33cb9f6f51abd6f7bd6e6d5685f491438f1384'
}

function Get-Checked([string]$Url, [string]$Sha, [string]$OutFile) {
    Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing
    $actual = (Get-FileHash -Algorithm SHA256 $OutFile).Hash.ToLowerInvariant()
    if ($actual -ne $Sha) {
        Remove-Item $OutFile
        throw "Checksum mismatch for $Url (got $actual)"
    }
}

$qdlDir = Join-Path $Dest 'qdl'
$driverDir = Join-Path $Dest 'drivers'
New-Item -ItemType Directory -Force -Path $qdlDir, $driverDir | Out-Null

$tmp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $archive = Join-Path $tmp 'qdl.tar.gz'
    Get-Checked $QdlUrl $QdlSha $archive
    # Relative paths: GNU tar would read "C:\..." as a remote host.
    Push-Location $tmp
    try { tar -xzf qdl.tar.gz qdl.exe LICENSE } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw 'Could not unpack qdl' }
    Copy-Item (Join-Path $tmp 'qdl.exe') $qdlDir -Force
    Copy-Item (Join-Path $tmp 'LICENSE') $qdlDir -Force
} finally {
    Remove-Item -Recurse -Force $tmp
}

foreach ($name in $DriverFiles.Keys) {
    Get-Checked "$DriverBase/$name" $DriverFiles[$name] (Join-Path $driverDir $name)
}
Write-Host "EDL tools are in $qdlDir and $driverDir"
