param([string]$NsisDirectory,[string]$OutputFile,[switch]$TestBuild)
$ErrorActionPreference='Stop'
$sourceRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $NsisDirectory){$found=Get-Command makensis.exe -ErrorAction SilentlyContinue;if($found){$NsisDirectory=Split-Path $found.Source}}
if(-not $NsisDirectory){throw 'NSIS 3.13 폴더를 -NsisDirectory로 지정하세요.'}
$compiler=Join-Path $NsisDirectory 'makensis.exe'
if(-not(Test-Path -LiteralPath $compiler)){throw 'makensis.exe가 없습니다.'}
if(-not $OutputFile){$OutputFile=Join-Path (Split-Path $sourceRoot) 'MDNote-Setup-2.3.0.exe'}
$OutputFile=[IO.Path]::GetFullPath($OutputFile)
if($OutputFile.StartsWith($sourceRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '설치 EXE는 소스 폴더 밖에 저장하세요.'}
$utf8=New-Object Text.UTF8Encoding($true)
$legacy=[Collections.Generic.List[string]]::new()
$legacy.Add('; Explicit old generated files only; no recursive folder deletion.')
foreach($relative in Get-Content -LiteralPath (Join-Path $PSScriptRoot 'legacy-files.txt') -Encoding UTF8){
 if(-not $relative){continue}
 if([IO.Path]::IsPathRooted($relative)-or $relative.Contains('..')-or $relative.Contains('*')-or $relative.Contains('"')-or $relative.Contains('$')){throw 'Unsafe legacy manifest entry'}
 if($relative.EndsWith('\')){$legacy.Add('RMDir "$INSTDIR\'+$relative.TrimEnd('\')+'"')}else{$legacy.Add('Delete "$INSTDIR\'+$relative+'"')}
}
[IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'legacy-cleanup.nsh'),$legacy,$utf8)
[IO.File]::WriteAllText((Join-Path $PSScriptRoot 'uninstall-files.nsh'),'; generated',$utf8)
$tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$packageRoot=Join-Path $tempBase ('MDNotePackage-'+[Guid]::NewGuid().ToString('N'))
try {
 New-Item -ItemType Directory -Path (Join-Path $packageRoot 'installer') -Force | Out-Null
 # 2.0: only what the app needs is installed (app\ is the install root); help documents go to help\. Sources live on GitHub.
 $app=Join-Path $packageRoot 'app';New-Item -ItemType Directory -Path (Join-Path $app 'help') -Force | Out-Null
 foreach($name in @('MDNote.exe','LICENSE','THIRD_PARTY_NOTICES.txt')){Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $app $name)}
 Copy-Item -LiteralPath (Join-Path $sourceRoot 'lib\ICSharpCode.AvalonEdit.dll') -Destination (Join-Path $app 'ICSharpCode.AvalonEdit.dll')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'lib\WpfMath.dll') -Destination (Join-Path $app 'WpfMath.dll')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'lib\XamlMath.Shared.dll') -Destination (Join-Path $app 'XamlMath.Shared.dll')
 $help=@('사용법.txt','마크다운 작성 예제.md','example-image.png','example.en.md','example.ja.md','example.zh-CN.md','example.zh-TW.md','MDNote 사용법.md','MDNote-guide.en.md','MDNote-guide.ja.md','MDNote-guide.zh-CN.md','MDNote-guide.zh-TW.md')
 foreach($name in $help){if(-not(Test-Path -LiteralPath (Join-Path $sourceRoot $name))){throw ('Missing help file: '+$name)}}
 $help+=@(Get-ChildItem -LiteralPath $sourceRoot -File -Filter 'guide-*.png' | Where-Object {$_.Name -notlike 'guide-icons-*'} | ForEach-Object {$_.Name})
 foreach($name in $help){Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path (Join-Path $app 'help') $name)}
 # The installer icon is read at compile time only; it stays outside app\ so it is not installed.
 Copy-Item -LiteralPath (Join-Path $sourceRoot 'MDNote.ico') -Destination (Join-Path $packageRoot 'MDNote.ico')
 foreach($name in @('MDNote.nsi','NSIS-LICENSE.txt','languages.nsh','build-setup.ps1','test-setup.ps1','README.md','install-notice.txt','install-notice.en.txt','install-notice.ja.txt','install-notice.zh-CN.txt','install-notice.zh-TW.txt','legacy-files.txt','legacy-cleanup.nsh','uninstall-files.nsh')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path (Join-Path $packageRoot 'installer') $name)}
 $lines=[Collections.Generic.List[string]]::new()
 $lines.Add('; Installed whitelist files only. Never recursively remove install root.')
 foreach($file in Get-ChildItem -LiteralPath $app -File -Recurse | Sort-Object FullName){$relative=$file.FullName.Substring($app.Length+1);$lines.Add('Delete "$INSTDIR\'+$relative+'"')}
 foreach($directory in Get-ChildItem -LiteralPath $app -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending){$relative=$directory.FullName.Substring($app.Length+1);$lines.Add('RMDir "$INSTDIR\'+$relative+'"')}
 [IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'uninstall-files.nsh'),$lines,$utf8)
 [IO.File]::WriteAllLines((Join-Path $packageRoot 'installer\uninstall-files.nsh'),$lines,$utf8)
 $compilerArgs=@('/V3',('/DPAYLOAD='+$packageRoot),('/DOUTPUT='+$OutputFile))
 if($TestBuild){$compilerArgs+='/DTEST_BUILD'}
 $compilerArgs+=(Join-Path $packageRoot 'installer\MDNote.nsi')
 & $compiler @compilerArgs
 if($LASTEXITCODE -ne 0){throw 'NSIS build failed.'}
 Get-Item -LiteralPath $OutputFile | Select-Object FullName,Length
 Get-FileHash -LiteralPath $OutputFile -Algorithm SHA256
} finally {
 # Only remove the exact newly-created package directory under the system temp root.
 $resolved=[IO.Path]::GetFullPath($packageRoot)
 if(([IO.Path]::GetDirectoryName($resolved)-eq $tempBase)-and([IO.Path]::GetFileName($resolved)-match '^MDNotePackage-[a-f0-9]{32}$')-and(Test-Path -LiteralPath $resolved)){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
