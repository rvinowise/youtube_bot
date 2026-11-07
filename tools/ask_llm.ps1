<#
.SYNOPSIS
    Ask another LLM (via OpenRouter) a question from the command line.

.DESCRIPTION
    Thin wrapper around https://openrouter.ai/api/v1/chat/completions.
    Useful when a tool error, a stack trace or a "how do I ...?" question is
    faster to hand to a model than to dig through docs.

    API key lookup order:
      1. -ApiKey parameter
      2. $env:OPENROUTER_API_KEY
      3. tools/openrouter.key   (git-ignored)
      4. $env:USERPROFILE\.openrouter.key

.EXAMPLE
    powershell -NoProfile -File tools\ask.ps1 -Prompt "Why does yt-dlp say 'n challenge solving failed'?"

.EXAMPLE
    Get-Content work\err.txt | powershell -NoProfile -File tools\ask.ps1 -System "You are a DevOps expert."

.EXAMPLE
    powershell -NoProfile -File tools\ask.ps1 -PromptFile work\err.txt -Files src\Scraping\YtDlp.fs -Model "anthropic/claude-3.5-sonnet"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Prompt,

    [string]$PromptFile,

    [string]$System = 'You are an expert DevOps and CLI engineer. Provide clean, working fixes.',

    [string]$Model = 'google/gemini-2.5-flash',

    [string[]]$Files,

    [string]$ApiKey,

    [double]$Temperature = 0.2,

    [int]$MaxTokens = 8000,

    [string]$Out,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Resolve-Key {
    param([string]$Explicit)
    if ($Explicit) { return $Explicit }
    if ($env:OPENROUTER_API_KEY) { return $env:OPENROUTER_API_KEY }
    $local = Join-Path $PSScriptRoot 'openrouter.key'
    if (Test-Path $local) { return (Get-Content $local -Raw).Trim() }
    $home_ = Join-Path $env:USERPROFILE '.openrouter.key'
    if (Test-Path $home_) { return (Get-Content $home_ -Raw).Trim() }
    throw "No OpenRouter API key. Set `$env:OPENROUTER_API_KEY or put it in tools/openrouter.key"
}

# ---- assemble the user message -------------------------------------------------
$parts = New-Object System.Collections.Generic.List[string]

if ($PromptFile) {
    if (-not (Test-Path $PromptFile)) { throw "PromptFile not found: $PromptFile" }
    $Prompt = Get-Content $PromptFile -Raw
}

# pipeline / redirect input (only when nothing was passed explicitly)
if (-not $Prompt -and -not [Console]::IsInputRedirected -eq $false) { }

$stdin = ''
if ([Console]::IsInputRedirected) {
    $stdin = [Console]::In.ReadToEnd()
}

if ($Prompt) { $parts.Add($Prompt) }
if ($stdin -and $stdin.Trim() -and $stdin.Trim() -ne ($Prompt -as [string])) { $parts.Add($stdin) }

foreach ($f in $Files) {
    if (-not (Test-Path $f)) { throw "File not found: $f" }
    $text = Get-Content $f -Raw
    $parts.Add("===== FILE: $f =====" + [Environment]::NewLine + $text)
}

if ($parts.Count -eq 0) {
    Write-Error "Nothing to ask. Provide -Prompt, -PromptFile or pipe text in."
    exit 1
}

$userText = ($parts -join ([Environment]::NewLine + [Environment]::NewLine))
if ($userText.Length -gt 200000) {
    Write-Warning ("Prompt is {0} chars, truncating to 200000" -f $userText.Length)
    $userText = $userText.Substring(0, 200000)
}

# ---- request -------------------------------------------------------------------
$body = @{
    model       = $Model
    messages    = @(
        @{ role = 'system'; content = $System }
        @{ role = 'user'; content = $userText }
    )
    temperature = $Temperature
    max_tokens  = $MaxTokens
} | ConvertTo-Json -Depth 6 -Compress

$headers = @{
    'Authorization' = "Bearer $(Resolve-Key $ApiKey)"
    'Content-Type'  = 'application/json'
    'HTTP-Referer'  = 'https://github.com/rvinowise/browsing'
    'X-Title'       = 'browsing/ask.ps1'
}

try {
    $resp = Invoke-RestMethod -Uri 'https://openrouter.ai/api/v1/chat/completions' `
        -Method Post -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($body)) -TimeoutSec 300
} catch {
    $detail = $_.Exception.Message
    $stream = $_.Exception.Response.GetResponseStream()
    if ($stream) {
        $reader = New-Object IO.StreamReader($stream)
        $detail = $reader.ReadToEnd()
        $reader.Dispose()
    }
    Write-Error "OpenRouter request failed: $detail"
    exit 1
}

if ($Json) {
    $text = $resp | ConvertTo-Json -Depth 20
} else {
    if (-not $resp.choices -or $resp.choices.Count -eq 0) {
        Write-Error ("No choices in response: " + ($resp | ConvertTo-Json -Depth 10))
        exit 1
    }
    $text = $resp.choices[0].message.content
}

Write-Output $text

if ($Out) {
    $dir = Split-Path -Parent $Out
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    Set-Content -Path $Out -Value $text -Encoding UTF8
    Write-Host ("[saved to {0}]" -f $Out) -ForegroundColor DarkGray
}

if ($resp.usage -and -not $Json) {
    Write-Host ("[model {0} | tokens in={1} out={2} | `$`{3}]{4}" -f `
        $resp.model, $resp.usage.prompt_tokens, $resp.usage.completion_tokens, `
        $resp.usage.total_cost, '') -ForegroundColor DarkGray
}
