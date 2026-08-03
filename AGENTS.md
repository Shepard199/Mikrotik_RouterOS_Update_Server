# Repository Instructions

These instructions are project-local for this repository.

## Scope
- Apply these instructions only inside this repository.
- Do not copy global Codex configuration or global memory into this project.
- Keep project-specific notes under `.codex/`.

## Working Rules
- Inspect only the files needed for the active task.
- Prefer small, reversible edits.
- Do not rewrite unrelated code or revert user changes unless explicitly asked.
- Use repository-provided build, test, and validation commands when available.
- Keep UI text localizable when changing web-facing strings.

## Bootstrap
- Use `.codex/BOOTSTRAP.md` for the project-local bootstrap workflow.
- Update project-local Codex notes only when repository facts change.

<!-- LOCAL-GRAPHIFY:BEGIN -->
## Graphify

This project uses Graphify as a repository knowledge graph stored under `graphify-out/`.

Graphify build, update, extraction, and clustering must use the project-local PowerShell wrapper so Graphify calls the local OpenAI-compatible model instead of the default Codex model.

Local Graphify model endpoint:

- `OPENAI_BASE_URL`: `http://172.27.0.95:8081/v1`
- `OPENAI_MODEL`: `qwythos-9b-claude-mythos-5-1m-q8_0`
- wrapper script: `.codex/graphify-local.ps1`

Do not switch the Codex model/provider just to run Graphify. Keep Codex on its configured model and invoke the wrapper for Graphify maintenance.

### Commands

For normal graph refresh after code changes, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\.codex\graphify-local.ps1 -Mode update -Path .
```

For full re-extraction / full graph rebuild, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\.codex\graphify-local.ps1 -Mode extract -Path .
```

For reclustering only, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\.codex\graphify-local.ps1 -Mode cluster -Path . -Resolution 1.0
```

Use full re-extraction only when the user explicitly asks for it, graph output is missing/corrupted/stale, many files changed, or Graphify query/path/explain results do not match the current code.

### Query-First Behavior

For codebase questions, prefer the existing graph before broad source browsing.

When `graphify-out/graph.json` exists:

- use `graphify query "<question>"` for architecture or codebase questions;
- use `graphify path "<A>" "<B>"` for relationships between files, modules, classes, or concepts;
- use `graphify explain "<concept>"` for focused explanations;
- use `graphify-out/wiki/index.md` for broad navigation if it exists.

Read `graphify-out/GRAPH_REPORT.md` only for broad architecture review, graph-level structure, or when query/path/explain is insufficient.

### Slash Command Routing

When the user types `/graphify`, do not invoke the default Graphify skill/tool automatically.

Instead, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\.codex\graphify-local.ps1 -Mode update -Path .
```

After the command completes, inspect only the relevant generated graph artifacts before answering.

### After Code Changes

After modifying source code, refresh the graph with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\.codex\graphify-local.ps1 -Mode update -Path .
```

Dirty `graphify-out/` files are expected after graph maintenance.
<!-- LOCAL-GRAPHIFY:END -->

