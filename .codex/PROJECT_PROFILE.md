# Project Profile: MikroTik.UpdateServer

Last updated: 2026-08-03

## Root
- Project root: `D:\GitHubHome\MikroTik.UpdateServer1`
- Solution: `MikroTik.UpdateServer.sln`
- Main project: `MikroTik.UpdateServer.csproj`
- Git status at bootstrap: repository exists, all files appear untracked in the new `.git` index.

## Detected Stack
- Runtime/framework: ASP.NET Core on `.NET 9.0` (`Microsoft.NET.Sdk.Web`)
- Application style: Minimal API with partial `Program` classes and service-layer helpers.
- UI: static web assets in `wwwroot` (`index.html`, `app.js`, `style.css`, `jquery-4.0.0.min.js`, `lang/en.json`, `lang/ru.json`)
- Observability/logging: Serilog, file/console sinks, local `ILogStore`, OpenTelemetry packages.
- Reliability/networking: Polly, response compression, rate limiting, IP whitelist middleware, health endpoints.
- Deployment: Dockerfile and `docker-compose.yml`, default container URL `http://+:5000`.
- Update-server behavior: on-demand RouterOS package download, version pointer routing, managed update orchestration, and version/architecture metadata in the web UI.

## Main Functional Areas
- RouterOS update/version management: `/api/versions`, `/api/update-check`, active version selection, version removal.
- MikroTik file serving: `/api/download/{version}/{filename}` and RouterOS-compatible static file flow.
- On-demand package fetches: missing RouterOS packages can be downloaded from MikroTik and then served to devices.
- Dashboard/logs: `/api/status`, `/api/logs`, `/api/logs/stats`, `/api/dashboard/clients-today`.
- Scheduling: `/api/schedule`, pause/resume, hosted update check service.
- Settings/localization: architecture filters, pointer routing, delete prefixes, v7 packages, language, console log settings.
- Health/diagnostics: `/health`, `/health/detailed`, `/health/connectivity`, `/health/disk`, `/health/filesystem`, `/health/downloads`, `/api/diagnostics`, `/api/health/tls`.

## Important Files
- `Program.cs`: application bootstrap, middleware, route registration, static file setup.
- `Program.*.cs`: grouped handlers for diagnostics, router/file serving, settings/schedule, updates, console logging.
- `Services/UpdateOrchestrator.cs`: current strategic update orchestration path.
- `Services/VersionManagementService.cs`: version metadata and active version management.
- `Services/OptimizedDownloadService.cs`: optimized file/download service path.
- `Services/FileBasedLogStore.cs`: persistent local log store used by dashboard/log APIs.
- `wwwroot/app.js`: main browser-side dynamic behavior.
- `wwwroot/lang/ru.json` and `wwwroot/lang/en.json`: UI localization.
- `appsettings.json`: production-style local configuration.
- `delete_prefixes.json`: deletion prefix settings copied to output.

## Engineering Constraints
- Treat the Go migration as abandoned unless the owner explicitly asks to restore it.
- Preserve C#/.NET implementation as the source of truth.
- Do not rewrite the frontend around jQuery unless it clearly reduces code and keeps existing behavior intact.
- Keep API response shapes compatible with current `wwwroot/app.js` expectations and MikroTik RouterOS clients.
- Keep Russian localization complete when UI text changes.
- Prefer scoped changes in `Program.*.cs`, `Services/*`, and `wwwroot/*` over large structural rewrites.
- Existing generated/build directories (`bin`, `obj`, `.dotnet-cli`, `.vs`) should generally not be edited manually.

## Selected Skills
- `build-web-apps:web-design-guidelines`: use for UI/accessibility/UX audits of `wwwroot`.
- `openai-docs`: available but not normally needed for this project.
- No dedicated .NET skill is installed in the local skill directory; use repository context plus Context7 MCP for library/API documentation when needed.

## Context7 Note
- Context7 is required by project instruction when library/API docs, setup, or configuration details are needed.
- Initial bootstrap attempted Context7 lookup for ASP.NET Core, but the MCP request failed with `fetch failed`; retry when concrete framework/API documentation is needed.
- Recent code changes validated on 2026-08-03 with `dotnet build .\MikroTik.UpdateServer.sln -nologo` succeeding with 0 warnings and 0 errors.

## Recommended Validation Commands
```powershell
dotnet restore .\MikroTik.UpdateServer.sln
dotnet build .\MikroTik.UpdateServer.sln -nologo
dotnet run --project .\MikroTik.UpdateServer.csproj
```

```powershell
docker compose build
docker compose up
```

```powershell
.\test_ip_whitelist.ps1
```

## Manual Smoke Checks
- Open `http://localhost:5000`.
- Verify Dashboard loads and `/api/status` updates.
- Verify language selector changes `wwwroot/lang/*.json` text.
- Trigger update check from UI and verify logs.
- Download a known RouterOS file through `/api/download/{version}/{filename}`.
- Check `GET /health` and `GET /health/detailed`.
