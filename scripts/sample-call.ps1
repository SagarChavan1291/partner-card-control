# Plays the partner: gets an access token, encrypts a PAN, locks and unlocks a card, then shows the
# card platform's audit trail. Needs the identity provider, both stubs and the API running (see README).
# Usage, from the repository root:  ./scripts/sample-call.ps1 [-Pan 4111111111111111] [-CorporateId CORP-1001]
# Works in Windows PowerShell 5.1 and PowerShell 7+.

param(
    [string] $Pan = '4111111111111111',
    [string] $CorporateId = 'CORP-1001',
    [string] $InitiatingUser = 'jane.smith@acme.example',
    [string] $Api = 'http://localhost:5190'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path .dev-keys/bank-dev-1.public.pem)) {
    Write-Host 'Creating the bank dev key pair (.dev-keys/)...'
    dotnet run tools/jwe-tool.cs -- keygen | Out-Null
}

$token = (Invoke-RestMethod -Method Post -Uri http://localhost:5090/oauth2/token -Body @{
    grant_type    = 'client_credentials'
    client_id     = 'partner-app'
    client_secret = 'dev-only-partner-app-secret'
    scope         = 'api://card-control/.default'
}).access_token

$encryptedPan = dotnet run tools/jwe-tool.cs -- encrypt --key .dev-keys/bank-dev-1.public.pem $Pan
if ($LASTEXITCODE -ne 0) { throw 'Encrypting the PAN failed.' }

function Invoke-CardAction([string] $action) {
    $request = @{
        Method      = 'Post'
        Uri         = "$Api/v1/cards/$action"
        Headers     = @{ Authorization = "Bearer $token"; 'X-Initiating-User' = $InitiatingUser }
        ContentType = 'application/json'
        Body        = (@{ encryptedPan = $encryptedPan; corporateId = $CorporateId } | ConvertTo-Json)
        UseBasicParsing = $true
    }
    try {
        $response = Invoke-WebRequest @request
        $status, $content = [int] $response.StatusCode, $response.Content
    }
    catch {
        # 4xx/5xx: still show the Problem Details body.
        if ($null -eq $_.Exception.Response) { throw }
        $status, $content = [int] $_.Exception.Response.StatusCode, $_.ErrorDetails.Message
        if (-not $content -and $_.Exception.Response -is [System.Net.WebResponse]) {
            # Windows PowerShell 5.1 doesn't always fill ErrorDetails; read the body from the response stream.
            $stream = $_.Exception.Response.GetResponseStream()
            if ($stream.CanSeek) { $stream.Position = 0 }
            $content = (New-Object System.IO.StreamReader($stream)).ReadToEnd()
        }
    }
    Write-Host "`nPOST /v1/cards/$action -> $status"
    Write-Host $content
}

Invoke-CardAction 'lock'
Invoke-CardAction 'unlock'

# The card token is internal: the API never returns it. Look it up from the stub's audit for display only.
Write-Host "`nCard platform audit trail (most recent 4):"
(Invoke-RestMethod http://localhost:5080/admin/audit) | Select-Object -Last 4 |
    Format-Table at, action, outcome, initiatingUser, channel -AutoSize
