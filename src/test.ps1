$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$resultFolder = Join-Path $PSScriptRoot 'test-results'
New-Item -ItemType Directory -Path $resultFolder -Force | Out-Null
$appPath = Join-Path $PSScriptRoot 'MDNote.exe'
$samplePath = Join-Path $PSScriptRoot '마크다운 작성 예제.md'
$imagePath = Join-Path $resultFolder 'preview.png'
$testProcess = Start-Process -FilePath $appPath -ArgumentList @('--selftest', ('"' + $samplePath + '"'), ('"' + $imagePath + '"')) -WindowStyle Hidden -Wait -PassThru
Get-Content -Encoding UTF8 -LiteralPath ($imagePath + '.result.txt')
if ($testProcess.ExitCode -ne 0) { throw 'MDNote self-test failed. See test-results.' }
Write-Output ('UI previews: ' + $resultFolder)
