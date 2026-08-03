# Validation Notes

## Standard Build
```powershell
dotnet restore .\MikroTik.UpdateServer.sln
dotnet build .\MikroTik.UpdateServer.sln -nologo
```

Last bootstrap result on 2026-05-13:
```text
dotnet build .\MikroTik.UpdateServer.sln -nologo
Build succeeded with 0 warnings and 0 errors.
```

## Local Run
```powershell
dotnet run --project .\MikroTik.UpdateServer.csproj
```

Expected local URL:
```text
http://localhost:5000
```

## Docker
```powershell
docker compose build
docker compose up
```

## Smoke Checks
- `GET /health`
- `GET /health/detailed`
- `GET /api/status`
- `GET /api/versions`
- `GET /api/logs/stats`
- `GET /api/settings/language`
- `GET /api/dashboard/clients-today`

## Frontend Checks
- Open `http://localhost:5000`.
- Confirm Dashboard cards update after API responses.
- Confirm language selector updates text from `wwwroot/lang/ru.json` or `wwwroot/lang/en.json`.
- Confirm Version Management actions call the expected API endpoints.
- Confirm logs page filters and ZIP export still work.

## Security/Network Checks
```powershell
.\test_ip_whitelist.ps1
```

For MikroTik access, verify DNS points `upgrade.mikrotik.com` to this server and required HTTP port is reachable from RouterOS clients.
