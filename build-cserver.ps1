# Windows build + tests, no .NET SDK needed (uses the csc.exe that ships with .NET Framework 4.x).
# Output: server\CServer.exe and tests\CServerTest.exe, then runs the tests (last line RESULT PASS n/n, exit code 0).
# Linux / Docker builds use server\CServer.csproj (net8.0); see AGENTS.md.
# -GameManaged <folder>: optional. Path to the game's oriDE_Data\Managed folder; if given, also checks that
#   shared\Arbiter.cs and shared\CsWire.cs compile against the game's .NET 3.5 mscorlib (they are compiled into the mod).
param([string]$GameManaged = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe (.NET Framework 4.x) not found' }
$sources = @(
    (Join-Path $here 'server\CServer.cs')
    (Join-Path $here 'shared\CsWire.cs')
    (Join-Path $here 'shared\Arbiter.cs')
    (Join-Path $here 'shared\net\NetPeer.cs')
    (Join-Path $here 'shared\net\Transport.cs')
)
$serverExe = Join-Path $here 'server\CServer.exe'
$testExe = Join-Path $here 'tests\CServerTest.exe'
$test = Join-Path $here 'tests\CServerTest.cs'

'Compiling CServer.exe...'
& $csc /nologo /target:exe /optimize+ /langversion:5 /out:$serverExe @sources
if ($LASTEXITCODE -ne 0) { throw 'CServer compilation failed' }

'Compiling CServerTest.exe...'
& $csc /nologo /target:exe /optimize+ /langversion:5 `
    /main:OriCoop.CServer.CServerTest /out:$testExe @sources $test
if ($LASTEXITCODE -ne 0) { throw 'CServerTest compilation failed' }

if ($GameManaged -ne '') {
    if (-not (Test-Path (Join-Path $GameManaged 'mscorlib.dll'))) { throw "no mscorlib.dll in $GameManaged" }
    'Checking shared\ against the game''s .NET 3.5 mscorlib...'
    $net35Out = Join-Path $env:TEMP 'oricoop-authority-net35.dll'
    & $csc /nologo /noconfig /nostdlib+ /target:library /langversion:5 "/out:$net35Out" `
        "/r:$GameManaged\mscorlib.dll" "/r:$GameManaged\System.dll" "/r:$GameManaged\System.Core.dll" `
        (Join-Path $here 'shared\CsWire.cs') (Join-Path $here 'shared\Arbiter.cs')
    if ($LASTEXITCODE -ne 0) { throw 'shared\ does not compile against .NET 3.5' }
}
else { 'SKIP .NET 3.5 check (pass -GameManaged <game>\oriDE_Data\Managed to run it)' }

'Running CServerTest.exe...'
& $testExe
exit $LASTEXITCODE
