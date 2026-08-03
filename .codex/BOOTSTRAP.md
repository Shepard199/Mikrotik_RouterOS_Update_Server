# Project Bootstrap Workflow

This bootstrap workflow is local to this repository.

## Purpose
Create lightweight project-local Codex state without importing global files verbatim.

## Steps
1. Detect the repository root from the current working directory or Git metadata.
2. Keep all project-specific state inside `.codex/`.
3. Create or update only project-local notes that are needed for the task.
4. Inspect source files only when the user asks for project analysis or implementation work.
5. Prefer repository-provided validation commands when source changes are made.
6. Report created or changed project-local instruction files clearly.

## Project-Local Files
- `AGENTS.md`: repository instructions for agents.
- `.codex/BOOTSTRAP.md`: this bootstrap workflow.
- `.codex/PROJECT_PROFILE.md`: optional project facts, only when analysis is requested.
- `.codex/memory/`: optional project-local memory, only when needed.
