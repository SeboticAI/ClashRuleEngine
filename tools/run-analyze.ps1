# Run the clash-analysis miner without fighting Python PATH issues.
# Finds a REAL Python (skipping the 0-byte Microsoft Store stub that prints
# "Python was not found"), then runs tools\analyze_clashes.py, passing through
# any arguments (e.g. an explicit clash_kinds.jsonl path).
#
#   .\tools\run-analyze.ps1                       # auto-find clash_kinds.jsonl on the Desktop
#   .\tools\run-analyze.ps1 "C:\path\clash.jsonl" # explicit input
#
# ASCII-only (PowerShell 5.1).

$ErrorActionPreference = "Stop"

function Get-RealPython {
    $cands = @()
    # 1. The per-user install on this machine.
    $cands += (Join-Path $env:LOCALAPPDATA "Python\bin\python.exe")
    # 2. Standard per-user CPython installs (python.org).
    $progPy = Join-Path $env:LOCALAPPDATA "Programs\Python"
    if (Test-Path $progPy) {
        $cands += (Get-ChildItem $progPy -Filter python.exe -Recurse -ErrorAction SilentlyContinue |
                   Select-Object -Expand FullName)
    }
    # 3. The py launcher and python/python3 as resolved on PATH (may be the real one).
    foreach ($n in 'py', 'python', 'python3') {
        $c = Get-Command $n -ErrorAction SilentlyContinue
        if ($c -and $c.Source) { $cands += $c.Source }
    }
    foreach ($p in $cands) {
        if (-not $p -or -not (Test-Path $p)) { continue }
        try { if ((Get-Item $p).Length -eq 0) { continue } } catch { }   # 0 bytes = Store stub
        try {
            $v = & $p --version 2>&1
            if ($v -match 'Python\s+3') { return $p }
        } catch { }
    }
    return $null
}

$py = Get-RealPython
if (-not $py) {
    Write-Error "No working Python 3 found. Install from https://www.python.org/downloads/ (tick 'Add to PATH'), then re-run."
    exit 1
}
Write-Host "Using Python: $py"

$script = Join-Path $PSScriptRoot "analyze_clashes.py"
& $py $script @args
exit $LASTEXITCODE
