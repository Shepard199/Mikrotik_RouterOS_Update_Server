param(
    [ValidateSet("update", "extract", "cluster")]
    [string]$Mode = "update",

    [string]$Path = ".",

    [double]$Resolution = 1.0
)

$ErrorActionPreference = "Stop"

$env:OPENAI_BASE_URL = "http://172.27.0.95:8081/v1"
$env:OPENAI_API_KEY = "sk-local"
$env:OPENAI_MODEL = "qwythos-9b-claude-mythos-5-1m-q8_0"

$env:GRAPHIFY_API_TIMEOUT = "900"
$env:GRAPHIFY_MAX_OUTPUT_TOKENS = "16384"

Write-Host "Graphify local mode: $Mode"
Write-Host "Path: $Path"
Write-Host "Model: $env:OPENAI_MODEL"
Write-Host "Base URL: $env:OPENAI_BASE_URL"

switch ($Mode) {
    "update" {
        graphify update $Path --force
    }

    "extract" {
        graphify extract $Path --backend openai --force --token-budget 4000 --max-concurrency 1 --api-timeout 900
    }

    "cluster" {
        graphify cluster-only $Path --backend openai --resolution $Resolution --exclude-hubs 99
    }
}
