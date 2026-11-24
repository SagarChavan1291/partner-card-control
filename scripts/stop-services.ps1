# Stops whatever .NET process is listening on the ports this project uses:
# 5090 (identity provider), 5070 (tokenisation), 5080 (card platform), 5190 (API).
# Works whatever started them: dotnet run, e2e-check.ps1 -KeepRunning, or Visual Studio.
# If they were started from an elevated (Administrator) session, run this from one too.
# Usage, from the repository root:  ./scripts/stop-services.ps1

$services = @(
    @{ Port = 5090; Name = 'identity provider' },
    @{ Port = 5070; Name = 'tokenisation stub' },
    @{ Port = 5080; Name = 'card platform stub' },
    @{ Port = 5190; Name = 'Card Control API' }
)

foreach ($service in $services) {
    $port = $service.Port; $label = "$port $($service.Name)"
    $listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) { Write-Host "  ${label}: not running"; continue }

    $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
    # Only stop .NET processes: never something unrelated that happens to use the port.
    $isDotnet = $process -and ($process.ProcessName -eq 'dotnet' -or
        $process.ProcessName -in 'MockIdentityProvider', 'TokenisationStub', 'CardPlatformStub', 'CardControl.Api')
    if (-not $isDotnet) {
        Write-Host "  ${label}: in use by '$($process.ProcessName)' (PID $($listener.OwningProcess)), not a .NET service; left alone" -ForegroundColor Yellow
        continue
    }

    try {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        Write-Host "  ${label}: stopped (PID $($process.Id))" -ForegroundColor Green
    }
    catch {
        Write-Host "  ${label}: couldn't stop PID $($process.Id) ($($_.Exception.Message))." -ForegroundColor Red
        Write-Host "        It was probably started as Administrator: run this script from an elevated PowerShell, or stop it in Visual Studio." -ForegroundColor Red
    }
}
