@echo off
REM IP Whitelist Testing Examples for Windows
REM Run these commands to test IP whitelist functionality

setlocal enabledelayedexpansion

echo ========================================
echo IP Whitelist Testing Examples (Windows)
echo ========================================
echo.

set API_URL=http://localhost:5000
set HEALTH_URL=http://localhost:5000/health

echo [Test 1] Health Check (Should always work - excluded endpoint)
echo Command: curl -i %HEALTH_URL%
echo.
curl -i %HEALTH_URL%
echo.
echo.

echo [Test 2] API Access with Allowed IP
echo Command: curl -i -H "X-Forwarded-For: 10.0.0.10" %API_URL%/api/status
echo.
curl -i -H "X-Forwarded-For: 10.0.0.10" %API_URL%/api/status
echo.
echo.

echo [Test 3] API Access with Denied IP
echo Command: curl -i -H "X-Forwarded-For: 203.0.113.99" %API_URL%/api/status
echo Expected: 403 Forbidden
echo.
curl -i -H "X-Forwarded-For: 203.0.113.99" %API_URL%/api/status
echo.
echo.

echo [Test 4] API Access with Allowed CIDR Range
echo Command: curl -i -H "X-Forwarded-For: 10.0.5.50" %API_URL%/api/logs
echo.
curl -i -H "X-Forwarded-For: 10.0.5.50" %API_URL%/api/logs
echo.
echo.

echo [Test 5] Localhost Access (Should work - allowed)
echo Command: curl -i http://127.0.0.1:5000/api/status
echo.
curl -i http://127.0.0.1:5000/api/status
echo.
echo.

echo [Test 6] IPv6 Localhost (Should work)
echo Command: curl -i http://[::1]:5000/api/status
echo.
curl -i http://[::1]:5000/api/status
echo.
echo.

echo [Test 7] X-Real-IP Header (Reverse Proxy Support)
echo Command: curl -i -H "X-Real-IP: 192.168.1.10" %API_URL%/api/status
echo.
curl -i -H "X-Real-IP: 192.168.1.10" %API_URL%/api/status
echo.
echo.

echo [Test 8] Multiple X-Forwarded-For IPs (Should use first)
echo Command: curl -i -H "X-Forwarded-For: 10.0.0.10, 203.0.113.99" %API_URL%/api/status
echo.
curl -i -H "X-Forwarded-For: 10.0.0.10, 203.0.113.99" %API_URL%/api/status
echo.
echo.

echo [Test 9] Private Network Range
echo Command: curl -i -H "X-Forwarded-For: 192.168.1.100" %API_URL%/api/status
echo.
curl -i -H "X-Forwarded-For: 192.168.1.100" %API_URL%/api/status
echo.
echo.

echo [Test 10] Metrics Endpoint (Should be excluded)
echo Command: curl -i -H "X-Forwarded-For: 203.0.113.99" %API_URL%/metrics
echo.
curl -i -H "X-Forwarded-For: 203.0.113.99" %API_URL%/metrics
echo.
echo.

echo ========================================
echo Tests Complete
echo ========================================
pause
