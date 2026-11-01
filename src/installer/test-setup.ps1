param([Parameter(Mandatory=$true)][string]$SetupFile,[Parameter(Mandatory=$true)][string]$TestRoot)
$ErrorActionPreference='Stop'
$TestRoot=[IO.Path]::GetFullPath($TestRoot)
if(Test-Path -LiteralPath $TestRoot){throw '시험 폴더는 새 전용 경로여야 합니다.'}
$regRoot='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\MDNoteInstallerSmokeTest'
if(Test-Path $regRoot){throw '기존 시험 설치를 먼저 제거하세요.'}
function Check($condition,$message){if(-not $condition){throw $message}}
function RunSetup {
    $process=Start-Process -FilePath $SetupFile -ArgumentList @('/S',('/D='+$TestRoot)) -WindowStyle Hidden -PassThru -Wait
    Check ($process.ExitCode -eq 0) ('Install exit code: '+$process.ExitCode)
}
function UninstallTest {
    $process=Start-Process -FilePath (Join-Path $TestRoot 'Uninstall.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru -Wait
    Check ($process.ExitCode -eq 0) ('Uninstall exit code: '+$process.ExitCode)
    for($i=0;$i -lt 40;$i++){
        if((-not(Test-Path (Join-Path $TestRoot 'Uninstall.exe')))-and(-not(Test-Path $regRoot))){break}
        Start-Sleep -Milliseconds 250
    }
    Check (-not(Test-Path $regRoot)) 'App registration remained after uninstall'
    Check (-not(Test-Path (Join-Path $TestRoot 'MDNote.exe'))) 'Application remained after uninstall'
    Check (-not(Test-Path 'HKCU:\Software\Classes\MDNoteInstallerSmokeTest.Document')) 'OpenWith ProgID remained'
}
$blockedRoot=$TestRoot+'-occupied'
if(Test-Path -LiteralPath $blockedRoot){throw '점유 폴더 시험 경로가 이미 있습니다.'}
New-Item -ItemType Directory -Path $blockedRoot | Out-Null
$blockedFile=Join-Path $blockedRoot '보존.txt'
[IO.File]::WriteAllText($blockedFile,'preserve')
$blocked=Start-Process -FilePath $SetupFile -ArgumentList @('/S',('/D='+$blockedRoot)) -WindowStyle Hidden -Wait -PassThru
Check ($blocked.ExitCode -ne 0) 'Unrelated nonempty folder was accepted'
Check ([IO.File]::ReadAllText($blockedFile) -eq 'preserve') 'Unrelated file was changed'
Remove-Item -LiteralPath $blockedFile
Remove-Item -LiteralPath $blockedRoot
RunSetup
$registered=Get-ItemProperty $regRoot
Check ($registered.InstallLocation -eq $TestRoot) 'Install location mismatch'
Check ($registered.DisplayName -eq 'MDNote (설치 검사)') 'App display name mismatch'
Check ($registered.UninstallString -eq ('"'+$TestRoot+'\Uninstall.exe"')) 'Uninstall command quoting mismatch'
Check (Test-Path (Join-Path $TestRoot '인계.md')) 'Handoff file missing'
Check (Test-Path (Join-Path $TestRoot 'installer\build-setup.ps1')) 'Installer source missing'
Check (-not(Test-Path (Join-Path $TestRoot 'test-results'))) 'Test screenshots were packaged'
Check (-not(Test-Path (Join-Path $TestRoot '작업중-v1.5.md'))) 'Scratch note was packaged'
Check (-not(Test-Path (Join-Path $TestRoot '검증기록.txt'))) 'Verification log was packaged'
$legacyNote=Join-Path $TestRoot '작업중-v1.5.md'
[IO.File]::WriteAllText($legacyNote,'old installed scratch note')
$legacyFolder=Join-Path $TestRoot 'test-results'
New-Item -ItemType Directory -Path $legacyFolder | Out-Null
$legacyImage=Join-Path $legacyFolder 'preview.png'
[IO.File]::WriteAllText($legacyImage,'old installed image')
$keptInside=Join-Path $legacyFolder '내가 추가한 문서.md'
[IO.File]::WriteAllText($keptInside,'keep this user file')
$sentinel=Join-Path $TestRoot '내 문서 보존 검사.md'
[IO.File]::WriteAllText($sentinel,'사용자 문서 보존 확인')
$lockedFile=Join-Path $TestRoot 'MDNote.exe'
$lockedStream=[IO.File]::Open($lockedFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try {
    $locked=Start-Process -FilePath $SetupFile -ArgumentList @('/S',('/D='+$TestRoot)) -WindowStyle Hidden -Wait -PassThru
    Check ($locked.ExitCode -ne 0) 'In-use executable did not stop installation'
} finally {$lockedStream.Dispose()}
RunSetup
Check ([IO.File]::ReadAllText($sentinel) -eq '사용자 문서 보존 확인') 'Reinstall changed user document'
Check (-not(Test-Path -LiteralPath $legacyNote)) 'Old scratch note was not cleaned on update'
Check (-not(Test-Path -LiteralPath $legacyImage)) 'Old installed image was not cleaned on update'
Check ([IO.File]::ReadAllText($keptInside) -eq 'keep this user file') 'Upgrade deleted user file in old test folder'
UninstallTest
Check ([IO.File]::ReadAllText($sentinel) -eq '사용자 문서 보존 확인') 'Uninstall deleted user document'
Check ([IO.File]::ReadAllText($keptInside) -eq 'keep this user file') 'Uninstall deleted user file in old test folder'
Check (@(Get-ChildItem -LiteralPath $TestRoot -Force).Count -eq 2) 'Unexpected installed files remained'
Remove-Item -LiteralPath $keptInside
Remove-Item -LiteralPath $legacyFolder
Remove-Item -LiteralPath $sentinel
Remove-Item -LiteralPath $TestRoot
RunSetup
UninstallTest
Check (-not(Test-Path -LiteralPath $TestRoot)) 'Empty install folder remained'
Check (-not(Test-Path 'HKCU:\Software\MDNoteInstallerSmokeTest')) 'Installer registry remained'
'PASS: custom install path, unrelated nonempty folder rejection, in-use file protection, Apps registration, quoted uninstall command, source handoff payload, same-path upgrade, user document preservation, uninstall registry cleanup, empty folder removal.'
