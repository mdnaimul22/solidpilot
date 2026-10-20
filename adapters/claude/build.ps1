# Build SolidworksExecution with the portable Roslyn compiler — the only working build path on this
# machine (no Visual Studio, no .NET SDK; the .NET Framework csc.exe is C# 5 only and rejects this
# source). Called by the `restart_execution_server` MCP tool (rebuild=True) and runnable by hand:
#     powershell -NoProfile -File "<this file>"
# Exit code 0 = the exe in bin\Debug is new. Any other exit code = the OLD exe is still on disk and
# is what a restart would run.
$ErrorActionPreference = "Stop"

$proj = "C:\Users\PC\.config\opencode\solidpilot\execution\solidworks\SolidworksExecution"
$bin  = Join-Path $proj "bin\Debug"
# Permanent home — NOT %TEMP% (a temp cleanup must not be able to break the build chain).
$csc  = "C:\Users\PC\.config\opencode\solidpilot\tools\roslyn\tools\csc.exe"
$fw   = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$api  = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"

if (-not (Test-Path $csc)) {
  throw "portable Roslyn compiler not found at $csc. Re-extract the Roslyn tools package there: the framework csc.exe is C# 5 only and cannot compile this source."
}

$refs = @(
  "$fw\System.dll",
  "$fw\System.Core.dll",
  "$fw\System.Net.Http.dll",
  "$fw\System.Xml.dll",
  "$fw\System.Configuration.dll",
  "$bin\System.Net.Http.Formatting.dll",
  "$bin\System.Web.Http.dll",
  "$bin\Microsoft.Owin.dll",
  "$bin\Microsoft.Owin.Host.HttpListener.dll",
  "$bin\Microsoft.Owin.Hosting.dll",
  "$bin\System.Web.Http.Owin.dll",
  "$bin\Owin.dll",
  "$bin\Newtonsoft.Json.dll",
  "$api\SolidWorks.Interop.sldworks.dll",
  "$api\SolidWorks.Interop.swconst.dll",
  "$api\SolidWorks.Interop.swpublished.dll"
) | ForEach-Object { if (-not (Test-Path $_)) { throw "missing reference: $_" }; "/r:`"$_`"" }

$src = Get-ChildItem $proj -Recurse -Filter *.cs |
  Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
  ForEach-Object { "`"$($_.FullName)`"" }

Write-Output "sources : $($src.Count)"
Write-Output "refs    : $($refs.Count)"

& $csc /nologo /noconfig /target:exe /platform:anycpu /debug:full `
  /define:"DEBUG;TRACE" /langversion:7.3 `
  /out:"$bin\SolidworksExecution.exe" /pdb:"$bin\SolidworksExecution.pdb" `
  $refs $src

if ($LASTEXITCODE -eq 0) {
  # A real MSBuild build copies the referenced interop (Private=True) into bin\Debug; do the same
  # so the runtime assembly version matches the compile-time reference exactly.
  Copy-Item "$api\SolidWorks.Interop.sldworks.dll" "$bin\SolidWorks.Interop.sldworks.dll" -Force
  Write-Output "copied interop -> $bin"
}
Write-Output "exit=$LASTEXITCODE"
exit $LASTEXITCODE
