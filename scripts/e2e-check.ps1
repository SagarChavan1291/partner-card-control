# End-to-end check against the REAL services: starts the mock identity provider, both stubs and the API,
# plays the partner through every main scenario, checks the card platform's audit trail and fault handling,
# scans the API's logs for PANs, then stops everything.
#
# Usage, from the repository root (Windows PowerShell 5.1 or PowerShell 7):
#   ./scripts/e2e-check.ps1                  # start everything, test, stop
#   ./scripts/e2e-check.ps1 -KeepRunning     # leave the services running afterwards (for manual poking)
#   ./scripts/e2e-check.ps1 -UseRunning      # don't start anything: test services you already started
# Exit code 0 when every check passes. Logs go to .e2e/logs/.

param(
    [switch] $KeepRunning,
    [switch] $UseRunning
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$Idp = 'http://localhost:5090'; $Tok = 'http://localhost:5070'; $Cards = 'http://localhost:5080'; $Api = 'http://localhost:5190'
$User = 'e2e.tester@acme.example'
$workDir = Join-Path $root '.e2e'
$logDir = Join-Path $workDir 'logs'
$script:passed = 0; $script:failed = 0; $script:started = @()

# ---------------------------------------------------------------- helpers

function Write-Step([string] $text) { Write-Host "`n== $text" -ForegroundColor Cyan }

function Test-Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { $script:passed++; Write-Host "  PASS  $name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "  FAIL  $name  $detail" -ForegroundColor Red }
}

function Invoke-Http([string] $method, [string] $uri, [hashtable] $headers = @{}, [string] $body = $null,
    [string] $contentType = 'application/json') {
    $request = @{ Method = $method; Uri = $uri; Headers = $headers; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($null -ne $body -and $body -ne '') { $request.Body = $body; $request.ContentType = $contentType }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $r = Invoke-WebRequest @request
        $status = [int] $r.StatusCode; $content = $r.Content
    }
    catch {
        if ($null -eq $_.Exception.Response) { return [pscustomobject]@{ Status = 0; Body = $_.Exception.Message; Json = $null; Seconds = 0 } }
        $status = [int] $_.Exception.Response.StatusCode; $content = $_.ErrorDetails.Message
        if (-not $content -and $_.Exception.Response -is [System.Net.WebResponse]) {
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream.CanSeek) { $stream.Position = 0 }
            $content = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
        }
    }
    $json = $null
    if ($content) { try { $json = $content | ConvertFrom-Json } catch { } }
    [pscustomobject]@{ Status = $status; Body = $content; Json = $json; Seconds = $watch.Elapsed.TotalSeconds }
}

function Test-Healthy([string] $baseUrl) {
    try { (Invoke-WebRequest "$baseUrl/healthz" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { $false }
}

function Get-Token([string] $clientId, [string] $secret, [string] $scope = 'api://card-control/.default', [string] $lifetime = $null) {
    $form = @{ grant_type = 'client_credentials'; client_id = $clientId; client_secret = $secret; scope = $scope }
    if ($lifetime) { $form.lifetime_seconds = $lifetime }
    (Invoke-RestMethod -Method Post -Uri "$Idp/oauth2/token" -Body $form).access_token
}

function Protect-Pan([string] $pan, [string] $keyPath = '.dev-keys/bank-dev-1.public.pem') {
    $jwe = dotnet run tools/jwe-tool.cs -- encrypt --key $keyPath $pan
    if ($LASTEXITCODE -ne 0) { throw "jwe-tool failed to encrypt a PAN" }
    ($jwe | Out-String).Trim()
}

function Invoke-Card([string] $action, [string] $jwe, [string] $corporateId, [string] $token = $script:token,
    [string] $initiatingUser = $User) {
    $headers = @{}
    if ($token) { $headers.Authorization = "Bearer $token" }
    if ($initiatingUser) { $headers['X-Initiating-User'] = $initiatingUser }
    $body = @{ encryptedPan = $jwe; corporateId = $corporateId } | ConvertTo-Json
    Invoke-Http Post "$Api/v1/cards/$action" $headers $body
}

function Test-Response([string] $name, $response, [int] $status, [string] $code = $null) {
    $ok = $response.Status -eq $status
    if ($ok -and $code) { $ok = $response.Json -and $response.Json.code -eq $code }
    $got = "got $($response.Status)"
    if ($response.Json -and $response.Json.code) { $got += " $($response.Json.code)" }
    $want = "$status"
    if ($code) { $want += " $code" }
    Test-Check "$name -> $want" $ok "($got)"
}

# Windows PowerShell 5.1 returns a JSON array from Invoke-RestMethod as one object; piping unrolls it.
function Get-JsonArray([string] $uri) {
    $result = Invoke-RestMethod $uri
    , @($result | ForEach-Object { $_ })
}

function Set-Faults([int] $latencyMs, [double] $errorRate) {
    $body = @{ latencyMs = $latencyMs; errorRate = $errorRate } | ConvertTo-Json
    Invoke-RestMethod -Method Put -Uri "$Cards/admin/faults" -ContentType 'application/json' -Body $body | Out-Null
}

function Start-Service([string] $name, [string] $projectDir, [string] $dll, [string] $url, [hashtable] $env = @{}) {
    $saved = @{}
    foreach ($k in $env.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $env[$k]) }
    try {
        # Run the built dll directly (not 'dotnet run'), so the PID is the service itself and stopping it is clean.
        $p = Start-Process dotnet -ArgumentList @("bin/Debug/net10.0/$dll", '--urls', $url) -WorkingDirectory $projectDir `
            -RedirectStandardOutput (Join-Path $logDir "$name.log") -RedirectStandardError (Join-Path $logDir "$name.err.log") `
            -WindowStyle Hidden -PassThru
        $script:started += $p
    }
    finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) }
    }
}

function Stop-Services {
    foreach ($p in $script:started) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
}

# ---------------------------------------------------------------- setup

try {
    Write-Step 'Prerequisites'
    $sdks = dotnet --list-sdks
    Test-Check '.NET 10 SDK installed' ([bool]($sdks | Where-Object { $_ -match '^10\.' })) '(install the .NET 10 SDK)'
    if ($script:failed) { throw 'Missing prerequisites.' }

    New-Item -ItemType Directory -Force $logDir | Out-Null
    if (-not (Test-Path .dev-keys/bank-dev-1.private.pem)) {
        Write-Host '  Creating the bank dev key pair in .dev-keys/'
        dotnet run tools/jwe-tool.cs -- keygen | Out-Null
    }

    $services = @(
        @{ Name = 'identity-provider'; Dir = 'mock-identity-provider'; Dll = 'MockIdentityProvider.dll'; Url = $Idp },
        @{ Name = 'tokenisation'; Dir = 'tokenisation-stub'; Dll = 'TokenisationStub.dll'; Url = $Tok },
        @{ Name = 'card-platform'; Dir = 'card-platform-stub'; Dll = 'CardPlatformStub.dll'; Url = $Cards },
        @{ Name = 'card-control-api'; Dir = 'src/CardControl.Api'; Dll = 'CardControl.Api.dll'; Url = $Api }
    )

    if ($UseRunning) {
        Write-Step 'Using already-running services'
    }
    else {
        foreach ($s in $services) {
            if (Test-Healthy $s.Url) { throw "$($s.Name) is already running on $($s.Url). Stop it with ./scripts/stop-services.ps1, or run with -UseRunning." }
        }

        Write-Step 'Building the solution'
        dotnet build CardControl.slnx --nologo -v quiet | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

        Write-Step 'Starting services (logs in .e2e/logs/)'
        foreach ($s in $services) {
            $env = @{ ASPNETCORE_ENVIRONMENT = 'Development' }
            if ($s.Name -eq 'card-control-api') {
                # Everything at Trace, so the PAN scan at the end sees the most verbose logging possible.
                $env['Logging__LogLevel__Default'] = 'Trace'
                $env['Logging__LogLevel__Microsoft.AspNetCore'] = 'Trace'
                $env['Logging__LogLevel__Microsoft.AspNetCore.Authentication'] = 'Trace'
            }
            Start-Service $s.Name (Join-Path $root $s.Dir) $s.Dll $s.Url $env
        }
    }

    foreach ($s in $services) {
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Healthy $s.Url) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
        Test-Check "$($s.Name) is up at $($s.Url)" (Test-Healthy $s.Url) "(see .e2e/logs/$($s.Name).err.log)"
    }
    if ($script:failed) { throw 'Services did not start.' }

    Invoke-RestMethod -Method Post "$Cards/admin/reset" | Out-Null
    Invoke-RestMethod -Method Post "$Tok/admin/reset" | Out-Null

    Write-Step 'Preparing what the partner sends (tokens and encrypted PANs)'
    $script:token = Get-Token 'partner-app' 'dev-only-partner-app-secret'
    $readonlyToken = Get-Token 'partner-readonly' 'dev-only-partner-readonly-secret'
    $otherAudienceToken = Get-Token 'partner-app' 'dev-only-partner-app-secret' 'api://other-api/.default'
    $expiredToken = Get-Token 'partner-app' 'dev-only-partner-app-secret' -lifetime '-60'

    $pans = @{}
    foreach ($pan in '4111111111111111', '4242424242424242', '4012888888881881', '4000056655665556',
                     '4000002500003155', '4000000000003220', '5555555555554444', '4111111111111112') {
        $pans[$pan] = Protect-Pan $pan
    }
    # A JWE for a key the bank doesn't hold (as if encrypted with someone else's public key).
    dotnet run tools/jwe-tool.cs -- keygen --kid not-the-bank --out (Join-Path $workDir 'keys') --force | Out-Null
    $foreignJwe = Protect-Pan '4111111111111111' (Join-Path $workDir 'keys/not-the-bank.public.pem')
    # The real JWE with one character of the ciphertext changed.
    $parts = $pans['4111111111111111'].Split('.')
    $parts[3] = $(if ($parts[3][0] -eq 'A') { 'B' } else { 'A' }) + $parts[3].Substring(1)
    $tamperedJwe = $parts -join '.'
    Write-Host '  Got 4 tokens and 10 encrypted PANs'

    # ---------------------------------------------------------------- checks

    Write-Step 'Health'
    Test-Response 'GET /healthz without a token' (Invoke-Http Get "$Api/healthz") 200

    Write-Step 'Partner authentication'
    Test-Response 'No token' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -token $null) 401
    Test-Response 'Token for another audience' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -token $otherAudienceToken) 401
    Test-Response 'Expired token' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -token $expiredToken) 401
    Test-Response 'Garbage token' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -token 'not.a.jwt') 401
    Test-Response 'Valid token without CardControl.Write (partner-readonly)' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -token $readonlyToken) 403

    Write-Step 'Lock and unlock'
    $r = Invoke-Card lock $pans['4111111111111111'] 'CORP-1001'
    Test-Response 'Lock 4111 (CORP-1001, Active)' $r 200
    Test-Check 'Response shows Locked, masked PAN, no card token' ($r.Json.status -eq 'Locked' -and $r.Json.maskedPan -eq '411111******1111' -and $r.Body -notmatch 'ctok_') "($($r.Body))"
    Test-Response 'Lock 4111 again' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001') 409 'CardAlreadyLocked'
    $r = Invoke-Card unlock $pans['4111111111111111'] 'CORP-1001'
    Test-Response 'Unlock 4111' $r 200
    Test-Check 'Response shows Active' ($r.Json.status -eq 'Active') "($($r.Body))"
    Test-Response 'Unlock 4111 again' (Invoke-Card unlock $pans['4111111111111111'] 'CORP-1001') 409 'CardNotLocked'
    Test-Response 'Unlock 4242 (CORP-1001, seeded Locked)' (Invoke-Card unlock $pans['4242424242424242'] 'CORP-1001') 200
    Test-Response 'Lock 1881 (CORP-2002, the other enabled corporate)' (Invoke-Card lock $pans['4012888888881881'] 'CORP-2002') 200
    Test-Response 'Lock 5556 (CORP-2002, Cancelled)' (Invoke-Card lock $pans['4000056655665556'] 'CORP-2002') 422 'CardCancelled'

    Write-Step 'Entitlement'
    Test-Response 'Lock 3155 as CORP-3003 (partner not enabled)' (Invoke-Card lock $pans['4000002500003155'] 'CORP-3003') 403 'CorporateNotEnabled'
    $notYours = Invoke-Card lock $pans['4000002500003155'] 'CORP-1001'
    Test-Response "Lock 3155 claiming CORP-1001 (it's CORP-3003's)" $notYours 404 'CardNotFound'
    Test-Response "Lock 1881 claiming CORP-1001 (it's CORP-2002's)" (Invoke-Card lock $pans['4012888888881881'] 'CORP-1001') 404 'CardNotFound'
    Test-Response 'Lock 3220 (card with no corporate)' (Invoke-Card lock $pans['4000000000003220'] 'CORP-1001') 404 'CardNotFound'
    $unknown = Invoke-Card lock $pans['5555555555554444'] 'CORP-1001'
    Test-Response 'Lock 4444 (valid PAN, no such card)' $unknown 404 'CardNotFound'
    Test-Check '"Not yours" and "does not exist" look the same' ($notYours.Json.title -eq $unknown.Json.title -and $notYours.Json.detail -eq $unknown.Json.detail)
    Test-Response 'Corporate ID is case-sensitive (corp-1001)' (Invoke-Card lock $pans['4111111111111111'] 'corp-1001') 403 'CorporateNotEnabled'

    Write-Step 'Encrypted PAN'
    Test-Response 'Clear PAN instead of a JWE' (Invoke-Card lock '4111111111111111' 'CORP-1001') 400 'InvalidEncryptedPan'
    Test-Response 'Tampered JWE' (Invoke-Card lock $tamperedJwe 'CORP-1001') 400 'InvalidEncryptedPan'
    Test-Response "JWE for a key the bank doesn't hold" (Invoke-Card lock $foreignJwe 'CORP-1001') 400 'InvalidEncryptedPan'
    Test-Response 'JWE of a number failing the Luhn check' (Invoke-Card lock $pans['4111111111111112'] 'CORP-1001') 400 'InvalidPan'

    Write-Step 'Request validation'
    Test-Response 'No X-Initiating-User' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -initiatingUser $null) 400 'InvalidRequest'
    Test-Response 'X-Initiating-User over 128 characters' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001' -initiatingUser ('a' * 129)) 400 'InvalidRequest'
    Test-Response 'Missing corporateId' (Invoke-Http Post "$Api/v1/cards/lock" @{ Authorization = "Bearer $script:token"; 'X-Initiating-User' = $User } "{`"encryptedPan`":`"x`"}") 400 'InvalidRequest'
    Test-Response 'Malformed JSON' (Invoke-Http Post "$Api/v1/cards/lock" @{ Authorization = "Bearer $script:token"; 'X-Initiating-User' = $User } '{not json') 400 'InvalidRequest'

    Write-Step "Audit trail (card platform's record)"
    $audit = Get-JsonArray "$Cards/cards/ctok_8f3a1c2e9b7d4a01/audit"
    Test-Check '4111 has 4 attempts recorded (2 successes, 2 rejections)' ($audit.Count -eq 4) "(found $($audit.Count))"
    Test-Check 'Every attempt carries the initiating user and channel partner-app' (@($audit | Where-Object { $_.initiatingUser -ne $User -or $_.channel -ne 'partner-app' }).Count -eq 0)
    Test-Check 'Rejected attempts are audited too (CardAlreadyLocked, CardNotLocked)' ((@($audit.outcome) -contains 'CardAlreadyLocked') -and (@($audit.outcome) -contains 'CardNotLocked'))
    Test-Check "Cards the partner wasn't entitled to were never touched (3155, 3220)" (
        (Get-JsonArray "$Cards/cards/ctok_3a7f9c1e5b2d8a05/audit").Count -eq 0 -and (Get-JsonArray "$Cards/cards/ctok_7c2e5a9f3d1b6c06/audit").Count -eq 0)

    Write-Step 'Card platform faults'
    Set-Faults 0 1.0
    $r = Invoke-Card lock $pans['4111111111111111'] 'CORP-1001'
    Test-Response 'Card platform failing every call' $r 503 'ServiceUnavailable'
    Test-Check "Error doesn't say which internal service failed" ($r.Body -notmatch 'CardPlatform|card platform|5080')
    Set-Faults 3000 0
    $r = Invoke-Card lock $pans['4111111111111111'] 'CORP-1001'
    Test-Response 'Card platform taking 3 s per call (attempt timeout 2 s)' $r 504 'GatewayTimeout'
    Test-Check 'Gave up within the 5 s total timeout' ($r.Seconds -lt 7) "(took $([math]::Round($r.Seconds, 1)) s)"
    Set-Faults 0 0.3
    $codes = 1..10 | ForEach-Object { (Invoke-Card lock $pans['4012888888881881'] 'CORP-2002').Status; (Invoke-Card unlock $pans['4012888888881881'] 'CORP-2002').Status }
    Test-Check '30% error rate: some calls still succeed, failures are clean 503s' (($codes -contains 200) -and @($codes | Where-Object { $_ -notin 200, 409, 503 }).Count -eq 0) "(statuses: $($codes -join ','))"
    Invoke-RestMethod -Method Post "$Cards/admin/reset" | Out-Null
    Test-Response 'Healthy again after reset' (Invoke-Card lock $pans['4111111111111111'] 'CORP-1001') 200

    Write-Step 'No PANs in the logs'
    if ($UseRunning) {
        Write-Host '  (skipped: with -UseRunning the API log is wherever you started it)'
    }
    else {
        Start-Sleep -Milliseconds 500   # let the log file flush
        $apiLog = (Get-Content (Join-Path $logDir 'card-control-api.log') -Raw) + (Get-Content (Join-Path $logDir 'card-control-api.err.log') -Raw)
        Test-Check 'API log was written at Trace level' ($apiLog -match 'trce:|dbug:') '(log looks empty or not verbose)'
        foreach ($pan in $pans.Keys) {
            Test-Check "No $($pan.Substring(12)) PAN in the API log" (-not $apiLog.Contains($pan) -and -not $apiLog.Contains($pan.Substring(0, 12)))
        }
    }
}
catch {
    $script:failed++
    Write-Host "`nERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($KeepRunning -or $UseRunning) {
        if ($script:started.Count) { Write-Host "`nServices left running (PIDs $(($script:started | ForEach-Object Id) -join ', '))." }
    }
    else {
        Stop-Services
    }
}

Write-Host ''
if ($script:failed -eq 0) {
    Write-Host "ALL $($script:passed) CHECKS PASSED" -ForegroundColor Green
    exit 0
}
Write-Host "$($script:failed) FAILED, $($script:passed) passed  (logs: .e2e/logs/)" -ForegroundColor Red
exit 1
