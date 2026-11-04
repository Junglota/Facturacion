param([string]$PublishDirectory = 'artifacts/publish', [switch]$Development, [switch]$AbruptGatewayStop)

$ErrorActionPreference = 'Stop'
$published = if ($Development) {
    (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../src/Integration.Api')).Path
} else { (Resolve-Path -LiteralPath $PublishDirectory).Path }
if (-not (Test-Path -LiteralPath (Join-Path $published 'wwwroot/index.html'))) {
    throw 'La publicación no contiene wwwroot/index.html. Ejecute dotnet publish primero.'
}
$testDirectory = Join-Path $PSScriptRoot ('../artifacts/check-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $testDirectory
$testDirectory = (Resolve-Path -LiteralPath $testDirectory).Path
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$environmentValues = @{
    API_KEY = 'published-test-key'
    SERVICE_API_KEY = 'published-internal-key'
    AUTH_DATABASE_PATH = (Join-Path $testDirectory 'users.db')
    ALLOW_LOCAL_SETUP = 'true'
    START_LOCAL_SERVICES = 'true'
    Inventory__DatabasePath = (Join-Path $testDirectory 'inventory.db')
    Sales__DatabasePath = (Join-Path $testDirectory 'sales.db')
}
$allocatedPorts = @($port)
foreach ($name in @('Inventory', 'Sales')) {
    do {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $servicePort = $listener.LocalEndpoint.Port
        $listener.Stop()
    } while ($allocatedPorts -contains $servicePort)
    $allocatedPorts += $servicePort
    $environmentValues[$name + '__Url'] = 'http://127.0.0.1:' + $servicePort
}
$previousEnvironment = @{}
$server = $null
$serviceProcesses = @()

try {
    foreach ($name in $environmentValues.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, $environmentValues[$name])
    }
    $arguments = if ($Development) {
        @('run', '--project', 'Integration.Api.csproj', '--no-build', '--no-launch-profile', '--', '--urls', $baseUrl)
    } else { @('Integration.Api.dll', '--urls', $baseUrl) }
    $server = Start-Process -FilePath (Get-Command dotnet).Source `
        -ArgumentList $arguments `
        -WorkingDirectory $published -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $testDirectory 'server.log') `
        -RedirectStandardError (Join-Path $testDirectory 'server-error.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($server.HasExited) { throw "La API terminó al iniciar. Revise $testDirectory" }
        try {
            $health = Invoke-WebRequest "$baseUrl/health" -UseBasicParsing -TimeoutSec 2
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'La API publicada no respondió a /health.' }
    if ($AbruptGatewayStop) {
        $serviceIds = @($allocatedPorts[1..2] | ForEach-Object {
            Get-NetTCPConnection -LocalPort $_ -State Listen -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess -Unique
        })
        if ($serviceIds.Count -ne 2 -or $serviceIds[0] -eq $serviceIds[1]) { throw 'No se pudieron identificar ambos procesos de servicio.' }
        $serviceProcesses = @(Get-Process -Id $serviceIds -ErrorAction Stop)
    }

    $page = Invoke-WebRequest $baseUrl -UseBasicParsing
    if ($page.StatusCode -ne 200 -or $page.Content -notmatch '<div id="app">') {
        throw 'La raíz no devuelve la página Vue.'
    }
    $resources = [regex]::Matches($page.Content, '(?:src|href)="([^"]+\.(?:js|css))"')
    if ($resources.Count -lt 2) { throw 'No se encontraron JavaScript y CSS compilados.' }
    foreach ($resource in $resources) {
        $path = $resource.Groups[1].Value
        if (-not $path.StartsWith('/assets/')) { throw "Recurso no compilado: $path" }
        $response = Invoke-WebRequest ($baseUrl + $path) -UseBasicParsing
        if ($response.StatusCode -ne 200) { throw "Recurso inaccesible: $path" }
        if ($path.EndsWith('.js') -and $response.Content -notmatch 'window\.location\.origin') {
            throw 'El cliente publicado no utiliza el origen del servidor.'
        }
    }

    $headers = @{ 'X-API-Key' = 'published-test-key' }
    $null = Invoke-WebRequest "$baseUrl/api/v1/products" -Headers $headers -UseBasicParsing
    $null = Invoke-WebRequest "$baseUrl/api/v1/sales" -Headers $headers -UseBasicParsing
    $created = Invoke-RestMethod "$baseUrl/api/v1/products" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"name":"Prueba publicada","priceCents":150000,"stock":10}'
    $saleBody = @{ productId = $created.id; quantity = 3; reference = 'PUBLISHED-001' } | ConvertTo-Json
    $sale = Invoke-RestMethod "$baseUrl/api/v1/sales" -Method Post -Headers $headers -ContentType 'application/json' -Body $saleBody
    if ($sale.status -ne 'Confirmed' -or $sale.totalCents -ne 450000) { throw 'La venta publicada no fue confirmada correctamente.' }
    $updated = Invoke-RestMethod "$baseUrl/api/v1/products/$($created.id)" -Headers $headers
    if ($updated.stock -ne 7) { throw 'Inventario no recibió el descuento de la venta.' }

    $session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    $browserHeaders = @{ 'X-Requested-With' = 'XMLHttpRequest' }
    $credentials = @{ username = 'admin-prueba'; password = 'clave-prueba-segura' } | ConvertTo-Json
    $null = Invoke-RestMethod "$baseUrl/api/auth/setup" -Method Post -Headers $browserHeaders -WebSession $session -ContentType 'application/json' -Body $credentials
    $null = Invoke-RestMethod "$baseUrl/api/auth/login" -Method Post -Headers $browserHeaders -WebSession $session -ContentType 'application/json' -Body $credentials
    $clientBody = @{ name = 'Empresa de prueba'; taxId = '101-00000-1'; accountingAccount = '1.1.02'; isActive = $true } | ConvertTo-Json
    $clientRecord = Invoke-RestMethod "$baseUrl/api/v1/clients" -Method Post -Headers $browserHeaders -WebSession $session -ContentType 'application/json' -Body $clientBody
    $sellerBody = @{ name = 'Vendedor de prueba'; commissionPercent = 12.35; isActive = $true } | ConvertTo-Json
    $sellerRecord = Invoke-RestMethod "$baseUrl/api/v1/sellers" -Method Post -Headers $browserHeaders -WebSession $session -ContentType 'application/json' -Body $sellerBody
    $articleBody = @{ description = 'Artículo de prueba'; unitPriceCents = 50000; isActive = $false; stock = 2 } | ConvertTo-Json
    $articleRecord = Invoke-RestMethod "$baseUrl/api/v1/articles" -Method Post -Headers $browserHeaders -WebSession $session -ContentType 'application/json' -Body $articleBody
    if (-not $clientRecord.id -or $sellerRecord.commissionPercent -ne 12.35 -or $articleRecord.isActive) { throw 'No se guardaron correctamente los nuevos catálogos.' }
    $null = Invoke-RestMethod "$baseUrl/api/auth/logout" -Method Post -Headers $browserHeaders -WebSession $session
    try {
        $null = Invoke-WebRequest "$baseUrl/api/v1/clients" -WebSession $session -UseBasicParsing
        throw 'El acceso continuó abierto después de cerrar sesión.'
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 401) { throw }
    }
    $null = Invoke-WebRequest "$baseUrl/swagger/v1/swagger.json" -UseBasicParsing
    try {
        $null = Invoke-WebRequest "$baseUrl/api/v1/products" -UseBasicParsing
        throw 'La API permitió consultar productos sin clave.'
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 401) { throw }
    }
    try {
        $null = Invoke-WebRequest "$baseUrl/api/v1/no-existe" -Headers $headers -UseBasicParsing
        throw 'La API no devolvió 404 para una ruta inexistente.'
    } catch {
        if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }
    }
    if ($AbruptGatewayStop) {
        $gatewayId = @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop | Select-Object -ExpandProperty OwningProcess -Unique)
        if ($gatewayId.Count -ne 1) { throw 'No se pudo identificar el proceso del gateway.' }
        # Solo se termina el gateway. Los servicios deben detectar su salida por sí mismos.
        $gatewayProcess = Get-Process -Id $gatewayId[0] -ErrorAction Stop
        $gatewayProcess.Kill()
        $gatewayProcess.WaitForExit(5000) | Out-Null
        $gatewayProcess.Dispose()
        foreach ($process in $serviceProcesses) {
            if (-not $process.WaitForExit(15000)) { throw "El proceso $($process.Id) permaneció activo después del cierre del gateway." }
        }
        Write-Output 'OK: los dos servicios terminaron al cerrar el gateway de forma abrupta.'
    }
    Write-Output 'OK: Vue, acceso, catálogos, gateway y microservicios; venta confirmada, stock actualizado, Swagger y rutas 404.'
} finally {
    if ($null -ne $server) {
        if (-not $server.HasExited) { $server.Kill($true); $server.WaitForExit(5000) | Out-Null }
        $server.Dispose()
    }
    foreach ($process in $serviceProcesses) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(5000) | Out-Null }
        $process.Dispose()
    }
    foreach ($name in $previousEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
}
