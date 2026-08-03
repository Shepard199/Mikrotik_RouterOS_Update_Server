# Project Memory

## Current Direction
- The Go migration has been explicitly abandoned by the owner.
- Keep working on the C#/.NET project in `D:\GitHubHome\MikroTik.UpdateServer1`.
- Preserve MikroTik RouterOS update-server behavior and web UI compatibility.

## Owner Preferences
- Communicate in Russian unless the owner asks otherwise.
- Use Context7 MCP whenever library/API documentation, code generation, setup, or configuration guidance is needed.
- Prefer direct fixes over long theoretical advice.
- Be careful with localization: all visible web UI text should be covered by localization files.

## Known Product Requirements
- RouterOS v6/v7 update pointer routing must reflect MikroTik branch behavior.
- Web dashboard should update dynamically, including download totals and clients-today details.
- Server console logging should be controllable from the web UI.
- Version Management should show downloaded architectures and allow deletion of unneeded versions.
- Front/back API contracts should stay compatible with `wwwroot/app.js`.

## Recent Project State
- Project root now contains a `.git` directory, but the index currently shows repository files as untracked.
- `go-server` and `GO_MIGRATION_CHECKLIST.md` were removed after the owner asked to forget the Go migration.
