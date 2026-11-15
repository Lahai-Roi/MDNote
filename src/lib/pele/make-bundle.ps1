# Rebuilds pele.chakra.min.js from the npm package "pele" for Windows chakra.dll (Edge 18 / ChakraCore 1.11 level).
# usage: make-bundle.ps1 -PeleGlobalJs <package\dist\pele.global.js> -Esbuild <esbuild.exe>
# Steps: esbuild down to ES2017 + minify, prepend polyfill.js (missing ES2019+ library methods), replace the one
# \p{L}\p{N} Unicode-property regex (chakra has no \p escapes) with an explicit range. After an update, run the
# Jint/ChakraCore differential check from work\validation\2.5.0\spike before shipping.
param([Parameter(Mandatory=$true)][string]$PeleGlobalJs,[Parameter(Mandatory=$true)][string]$Esbuild)
$ErrorActionPreference='Stop'
$here=$PSScriptRoot
$tmpDir=Join-Path $env:TEMP 'claude'; New-Item -ItemType Directory -Force $tmpDir | Out-Null
$tmp=Join-Path $tmpDir 'pele-es2017.js'
& $Esbuild $PeleGlobalJs --target=es2017 --minify ('--outfile='+$tmp) --log-level=warning
if($LASTEXITCODE -ne 0){throw 'esbuild failed'}
$js=[IO.File]::ReadAllText($tmp)
$old='[\p{L}\p{N}_-][\p{L}\p{N}_ .-]*'
$new='[A-Za-z0-9\u00AA-\uFFFF_-][A-Za-z0-9\u00AA-\uFFFF_ .-]*'
$count=([regex]::Matches($js,[regex]::Escape($old))).Count
if($count -ne 1){throw ('expected one \p regex, found '+$count)}
if(([regex]::Matches($js.Replace($old,''),'\\p\{')).Count -ne 0){throw 'another \p{ escape appeared; extend this script'}
$js=$js.Replace($old,$new)
$poly=[IO.File]::ReadAllText((Join-Path $here 'polyfill.js'))
[IO.File]::WriteAllText((Join-Path $here 'pele.chakra.min.js'),"var globalThis=this;`n"+$poly+$js,(New-Object Text.UTF8Encoding $false))
