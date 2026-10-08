<#
.SYNOPSIS
    Ogm icin uctan uca duman testi.

.DESCRIPTION
    Sunucuyu Release ciktisinden gecici olarak baslatir; saglik, panel
    statik dosyalari, ajan kaydi, heartbeat, multipart is yukleme, durum
    anlik goruntusu ve payload indirme uç noktalarini sinar; ardindan
    sunucuyu kapatir.

    Once derleme yapin:  dotnet build Ogm.sln -c Release

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-test.ps1
#>
[CmdletBinding()]
param(
    [string]$ServerExe = "",
    [string]$BaseUrl = "http://127.0.0.1:5099",
    [int]$ReadyTimeoutSeconds = 30
)

# (Varsayilan yolu burada hesapliyoruz; $PSScriptRoot parametre varsayilaninda
#  bazi PowerShell surumlerinde bos olabiliyor.)
if ([string]::IsNullOrWhiteSpace($ServerExe)) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $ServerExe = Join-Path $scriptDir "..\src\Ogm.Server\bin\Release\net9.0\Ogm.Server.exe"
}

$ErrorActionPreference = "Stop"
$script:failures = 0

function Test-Step {
    param([string]$Name, [scriptblock]$Action)

    try {
        $result = & $Action
        Write-Host ("  [OK]   {0,-9} {1}" -f $Name, $result) -ForegroundColor Green
    }
    catch {
        $script:failures++
        Write-Host ("  [HATA] {0,-9} {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
    }
}

$ServerExe = [System.IO.Path]::GetFullPath($ServerExe)
if (-not (Test-Path -LiteralPath $ServerExe)) {
    throw "Sunucu bulunamadi: $ServerExe`nOnce 'dotnet build Ogm.sln -c Release' calistirin."
}

# --- Test dosyalarini hazirla -------------------------------------------
$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ogm-smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

$payloadText = "SMOKE PRINT DATA"
$payloadPath = Join-Path $tempDir "sample.spl"
$metaPath = Join-Path $tempDir "meta.json"

Set-Content -LiteralPath $payloadPath -Value $payloadText -NoNewline -Encoding ascii

$agentId = "smoke-" + [Guid]::NewGuid().ToString("N").Substring(0, 6)
$jobId = "job-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)

$meta = [ordered]@{
    jobId          = $jobId
    agentId        = $agentId
    machineName    = $env:COMPUTERNAME
    userName       = $env:USERNAME
    printerName    = "Smoke Printer"
    documentName   = "deneme.docx"
    dataType       = "NT EMF 1.008"
    totalBytes     = $payloadText.Length
    totalPages     = 1
    sha256         = ("0" * 64)
    capturedUtc    = (Get-Date).ToUniversalTime().ToString("o")
    sourceFileName = "00012.SPL"
}
$meta | ConvertTo-Json -Compress | Set-Content -LiteralPath $metaPath -NoNewline -Encoding utf8

Write-Host "Ogm duman testi" -ForegroundColor Cyan
Write-Host ("  Sunucu: {0}" -f $ServerExe)
Write-Host ("  Adres : {0}" -f $BaseUrl)

$server = $null
try {
    # --- Sunucuyu baslat -------------------------------------------------
    $server = Start-Process -FilePath $ServerExe -ArgumentList @("--urls", $BaseUrl) -PassThru -WindowStyle Hidden

    $ready = $false
    $deadline = (Get-Date).AddSeconds($ReadyTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($server.HasExited) { throw ("Sunucu erken kapandi (cikis kodu {0})." -f $server.ExitCode) }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/health" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 400
        }
    }

    if (-not $ready) { throw "Sunucu $ReadyTimeoutSeconds saniye icinde ayaga kalkmadi." }

    # --- Saglik ve panel statik dosyalari --------------------------------
    Test-Step "health" {
        (Invoke-WebRequest -UseBasicParsing "$BaseUrl/health").Content
    }

    Test-Step "panel" {
        $response = Invoke-WebRequest -UseBasicParsing "$BaseUrl/"
        "{0}, {1} bayt" -f $response.StatusCode, $response.Content.Length
    }

    Test-Step "app.js" {
        (Invoke-WebRequest -UseBasicParsing "$BaseUrl/app.js").StatusCode
    }

    Test-Step "styles" {
        (Invoke-WebRequest -UseBasicParsing "$BaseUrl/styles.css").StatusCode
    }

    # --- Ajan kaydi ------------------------------------------------------
    $registerBody = [ordered]@{
        agentId         = $agentId
        machineName     = $env:COMPUTERNAME
        userName        = $env:USERNAME
        osVersion       = [System.Environment]::OSVersion.VersionString
        agentVersion    = "0.1.0-smoke"
        protocolVersion = 1
    } | ConvertTo-Json -Compress

    Test-Step "register" {
        (Invoke-WebRequest -UseBasicParsing -Method POST -ContentType "application/json" -Body $registerBody "$BaseUrl/api/agents/register").Content
    }

    # --- Heartbeat -------------------------------------------------------
    $heartbeatBody = [ordered]@{
        agentId        = $agentId
        pendingJobs    = 1
        sentJobs       = 0
        failedJobs     = 0
        spoolDirectory = "C:/Windows/System32/spool/PRINTERS"
        captureEnabled = $true
        timestampUtc   = (Get-Date).ToUniversalTime().ToString("o")
    } | ConvertTo-Json -Compress

    Test-Step "heartbeat" {
        (Invoke-WebRequest -UseBasicParsing -Method POST -ContentType "application/json" -Body $heartbeatBody "$BaseUrl/api/agents/heartbeat").StatusCode
    }

    # --- Multipart is yukleme (HttpClient ile, harici araca gerek yok) ---
    Test-Step "yukleme" {
        # Windows PowerShell 5.1'de System.Net.Http ayrica yuklenmelidir.
        Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue

        $client = [System.Net.Http.HttpClient]::new()
        try {
            $multipart = [System.Net.Http.MultipartFormDataContent]::new()

            $metaPart = [System.Net.Http.StringContent]::new((Get-Content -LiteralPath $metaPath -Raw))
            $multipart.Add($metaPart, "meta")

            $payloadBytes = [System.IO.File]::ReadAllBytes($payloadPath)
            $payloadPart = [System.Net.Http.ByteArrayContent]::new($payloadBytes)
            $payloadPart.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/octet-stream")
            $multipart.Add($payloadPart, "payload", "sample.spl")

            $response = $client.PostAsync("$BaseUrl/api/jobs", $multipart).GetAwaiter().GetResult()
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $response.EnsureSuccessStatusCode() | Out-Null
            $body
        }
        finally {
            $client.Dispose()
        }
    }

    # --- Durum anlik goruntusu -------------------------------------------
    Test-Step "state" {
        $state = (Invoke-WebRequest -UseBasicParsing "$BaseUrl/api/state").Content | ConvertFrom-Json
        $agentSeen = @($state.agents | Where-Object { $_.agentId -eq $agentId }).Count
        $jobSeen = @($state.jobs | Where-Object { $_.jobId -eq $jobId }).Count
        if ($agentSeen -ne 1) { throw "Ajan durumda gorunmuyor." }
        if ($jobSeen -ne 1) { throw "Is durumda gorunmuyor." }
        "ajan+is dogrulandi (onlineThreshold={0}s)" -f $state.onlineThresholdSeconds
    }

    # --- Payload indirme --------------------------------------------------
    Test-Step "payload" {
        $response = Invoke-WebRequest -UseBasicParsing "$BaseUrl/api/jobs/$jobId/payload"
        if ($response.RawContentLength -ne $payloadText.Length) {
            throw ("Beklenen {0} bayt, alinan {1} bayt." -f $payloadText.Length, $response.RawContentLength)
        }
        "{0} bayt indirildi" -f $response.RawContentLength
    }
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        Write-Host "  Sunucu kapatildi." -ForegroundColor DarkGray
    }
    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:failures -eq 0) {
    Write-Host "TUM TESTLER GECTI" -ForegroundColor Green
    exit 0
}
else {
    Write-Host ("{0} TEST BASARISIZ" -f $script:failures) -ForegroundColor Red
    exit 1
}
