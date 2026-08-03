# IP Whitelist Testing Examples for PowerShell
# Run these commands to test IP whitelist functionality

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "IP Whitelist Testing Examples (PowerShell)" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$API_URL = "http://localhost:5000"
$HEALTH_URL = "http://localhost:5000/health"

# Function to make requests with headers
function Test-Endpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TestName,

        [Parameter(Mandatory = $true)]
        [string]$Url,

        [string]$IpAddress,

        [string]$HeaderType = "X-Forwarded-For"
    )

    Write-Host "[Test] $TestName" -ForegroundColor Yellow
    Write-Host "URL: $Url" -ForegroundColor Gray
    if ($IpAddress) {
        Write-Host "Header: $HeaderType = $IpAddress" -ForegroundColor Gray
    }
    Write-Host ""

    $headers = @{}
    if ($IpAddress) {
        $headers[$HeaderType] = $IpAddress
    }

    try {
        # Если PS 7+ и хочется получать объект даже при 403, можно добавить:
        # -SkipHttpErrorCheck и работать без try/catch, проверяя .StatusCode вручную.
        $response = Invoke-WebRequest -Uri $Url -Headers $headers -ErrorAction Stop

        Write-Host "Status: $($response.StatusCode) $($response.StatusDescription)" -ForegroundColor Green
        Write-Host "Response:"
        Write-Host $response.Content
    }
    catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($resp) {
            $statusCode = [int]$resp.StatusCode
            $statusDescription = $resp.StatusDescription
            Write-Host "Status: $statusCode $statusDescription" -ForegroundColor Red

            try {
                $stream = $resp.GetResponseStream()
                $reader = New-Object System.IO.StreamReader($stream)
                $responseContent = $reader.ReadToEnd()
                $reader.Dispose()

                Write-Host "Response:"
                Write-Host $responseContent
            }
            catch {
                Write-Host "Error reading response body" -ForegroundColor Red
            }
        }
        else {
            Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    catch {
        Write-Host "Unexpected error: $($_.Exception.Message)" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host ""
}

# Test 1: Health Check (Should always work - excluded endpoint)
Test-Endpoint -TestName "Health Check (Should always work - excluded endpoint)" `
    -Url $HEALTH_URL

# Test 2: API Access with Allowed IP
Test-Endpoint -TestName "API Access with Allowed IP (10.0.0.10)" `
    -Url "$API_URL/api/status" `
    -IpAddress "10.0.0.10" `
    -HeaderType "X-Forwarded-For"

# Test 3: API Access with Denied IP
Test-Endpoint -TestName "API Access with Denied IP (203.0.113.99) - Should Return 403" `
    -Url "$API_URL/api/status" `
    -IpAddress "203.0.113.99" `
    -HeaderType "X-Forwarded-For"

# Test 4: API Access with Allowed CIDR Range
Test-Endpoint -TestName "API Access with IP in Allowed CIDR Range (10.0.5.50)" `
    -Url "$API_URL/api/logs" `
    -IpAddress "10.0.5.50" `
    -HeaderType "X-Forwarded-For"

# Test 5: Localhost Access
Test-Endpoint -TestName "Localhost Access (127.0.0.1)" `
    -Url "http://127.0.0.1:5000/api/status"

# Test 6: IPv6 Localhost
Test-Endpoint -TestName "IPv6 Localhost (::1)" `
    -Url "http://[::1]:5000/api/status"

# Test 7: X-Real-IP Header (Reverse Proxy Support)
Test-Endpoint -TestName "X-Real-IP Header (Reverse Proxy Support)" `
    -Url "$API_URL/api/status" `
    -IpAddress "192.168.1.10" `
    -HeaderType "X-Real-IP"

# Test 8: Multiple X-Forwarded-For IPs
Test-Endpoint -TestName "Multiple X-Forwarded-For IPs (Should use first)" `
    -Url "$API_URL/api/status" `
    -IpAddress "10.0.0.10, 203.0.113.99" `
    -HeaderType "X-Forwarded-For"

# Test 9: Private Network Range
Test-Endpoint -TestName "Private Network Range (192.168.1.100)" `
    -Url "$API_URL/api/status" `
    -IpAddress "192.168.1.100" `
    -HeaderType "X-Forwarded-For"

# Test 10: Metrics Endpoint (Should be excluded)
Test-Endpoint -TestName "Metrics Endpoint - Excluded from Whitelist (203.0.113.99)" `
    -Url "$API_URL/metrics" `
    -IpAddress "203.0.113.99" `
    -HeaderType "X-Forwarded-For"

# Test 11: Swagger (Should be excluded)
Test-Endpoint -TestName "Swagger Endpoint - Excluded from Whitelist (203.0.113.99)" `
    -Url "$API_URL/swagger" `
    -IpAddress "203.0.113.99" `
    -HeaderType "X-Forwarded-For"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Tests Complete" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
