param([switch]$NoRestore)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$envPath = Join-Path $repositoryRoot '.env'
if (-not (Test-Path -LiteralPath $envPath)) {
    throw 'Thiếu .env. Hãy sao chép .env.example và đặt POSTGRES_PASSWORD.'
}

$settings = @{}
foreach ($line in Get-Content -LiteralPath $envPath) {
    if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)=(.*)$') {
        $value = $Matches[2].Trim()
        if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $settings[$Matches[1]] = $value
    }
}

foreach ($name in @('POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD', 'POSTGRES_PORT')) {
    if ([string]::IsNullOrWhiteSpace($settings[$name])) {
        throw "Thiếu $name trong .env."
    }
}

if ($settings['POSTGRES_DB'] -eq 'rentalservice') {
    throw 'POSTGRES_DB đang trỏ tới Rental. Hãy chọn database Payment trước khi chạy API.'
}

$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=$($settings['POSTGRES_PORT']);Database=$($settings['POSTGRES_DB']);Username=$($settings['POSTGRES_USER']);Password=$($settings['POSTGRES_PASSWORD'])"
$env:ASPNETCORE_ENVIRONMENT = 'Development'

Push-Location $repositoryRoot
try {
    if ($NoRestore) {
        dotnet run --no-restore --no-launch-profile --project PaymentService/src/Api/PaymentService.Api.csproj --urls http://127.0.0.1:46280
    }
    else {
        dotnet run --no-launch-profile --project PaymentService/src/Api/PaymentService.Api.csproj --urls http://127.0.0.1:46280
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Payment API exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
