param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'RuntimeHook.c'
$output = Join-Path $OutputDirectory 'Unidot.RuntimeHook.dll'
if ((Test-Path -LiteralPath $output) -and (Get-Item -LiteralPath $output).LastWriteTimeUtc -ge (Get-Item -LiteralPath $source).LastWriteTimeUtc) { exit 0 }
$compiler = $env:UNIDOT_TCC
if (-not $compiler) {
    $command = Get-Command tcc.exe -ErrorAction SilentlyContinue
    if ($command) { $compiler = $command.Source }
}
if (-not $compiler -or -not (Test-Path -LiteralPath $compiler)) {
    throw 'Building the original-EXE runtime hook requires x64 TinyCC. Set UNIDOT_TCC to tcc.exe (see docs/mcp.md). End users do not need this compiler.'
}
if (-not (Test-Path -LiteralPath (Split-Path -Parent $OutputDirectory))) { throw 'Native output parent directory does not exist.' }
if (-not (Test-Path -LiteralPath $OutputDirectory)) { New-Item -ItemType Directory -Path $OutputDirectory | Out-Null }
& $compiler -shared -o $output $source -luser32
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
