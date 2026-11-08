Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "nsDialogs.nsh"
!include "FileFunc.nsh"
!ifndef PAYLOAD
 !error "PAYLOAD is required; run build-setup.ps1"
!endif
!ifndef OUTPUT
 !error "OUTPUT is required"
!endif
!define VERSION "2.3.1"
!ifdef TEST_BUILD
 !define APP_NAME "MDNote (설치 검사)"
 !define APP_KEY "MDNoteInstallerSmokeTest"
 !define APP_ID "MDNoteInstallerSmokeTest.Document"
 !define REG_ROOT HKCU
 !define SHELL_CONTEXT current
 RequestExecutionLevel user
 InstallDir "$LOCALAPPDATA\Programs\MDNoteInstallerSmokeTest"
!else
 !define APP_NAME "MDNote"
 !define APP_KEY "MDNote"
 !define APP_ID "MDNote.Document"
 !define REG_ROOT HKLM
 !define SHELL_CONTEXT all
 RequestExecutionLevel admin
 InstallDir "$PROGRAMFILES64\MDNote"
!endif
!define REG_APP "Software\${APP_KEY}\Installer"
!define REG_UNINSTALL "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_KEY}"
Name "${APP_NAME}"
OutFile "${OUTPUT}"
InstallDirRegKey ${REG_ROOT} "${REG_APP}" "InstallDir"
SetCompressor /SOLID lzma
SetOverwrite on
ShowInstDetails show
ShowUninstDetails show
ManifestDPIAware true
VIProductVersion "2.3.1.0"
VIAddVersionKey /LANG=1042 "ProductName" "$(MDNoteText16)"
VIAddVersionKey /LANG=1042 "FileDescription" "${APP_NAME} 설치 프로그램"
VIAddVersionKey /LANG=1042 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1042 "LegalCopyright" "MDNote"
!define MUI_ICON "${PAYLOAD}\MDNote.ico"
!define MUI_UNICON "${PAYLOAD}\MDNote.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "$(MDNoteText16)"
!define MUI_WELCOMEPAGE_TEXT "$(MDNoteText17)"
!insertmacro MUI_PAGE_WELCOME
!define MUI_PAGE_HEADER_TEXT "$(MDNoteText0)"
!define MUI_PAGE_HEADER_SUBTEXT "$(MDNoteText1)"
!define MUI_LICENSEPAGE_TEXT_TOP "$(MDNoteText2)"
!define MUI_LICENSEPAGE_TEXT_BOTTOM "$(MDNoteText3)"
!define MUI_LICENSEPAGE_BUTTON "$(MDNoteText4)"
!insertmacro MUI_PAGE_LICENSE "$(InstallNotice)"
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE ValidateDirectory
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TEXT "$(MDNoteText18)"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_FUNCTION CreateDesktopShortcut
!define MUI_FINISHPAGE_RUN_TEXT "$(MDNoteText5)"
; Start menu shortcuts are checked by default (user 2026-11-08).
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION CreateStartMenuShortcuts
!define MUI_FINISHPAGE_SHOWREADME_TEXT "$(MDNoteText6)"
; Third check box on the finish page (below the two MUI ones, MUI's own layout: 90u and 110u): the app's
; automatic update check, unchecked by default. The choice is stored as HKLM Software\MDNote AutoUpdateCheck.
!define MUI_PAGE_CUSTOMFUNCTION_SHOW FinishPageShow
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE FinishPageLeave
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
UninstPage custom un.SettingsPage un.SettingsLeave
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH
!insertmacro MUI_LANGUAGE "Korean"
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Japanese"
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "TradChinese"
!insertmacro MUI_RESERVEFILE_LANGDLL
!include "languages.nsh"

Var RemoveSettings
Var SettingsCheckbox
Var AutoUpdateCheckbox

Function FinishPageShow
 ${NSD_CreateCheckbox} 120u 130u 195u 10u "$(MDNoteText23)"
 Pop $AutoUpdateCheckbox
 SetCtlColors $AutoUpdateCheckbox "000000" "FFFFFF"
 ; Unchecked unless the user ticks it.
FunctionEnd
Function FinishPageLeave
 ${NSD_GetState} $AutoUpdateCheckbox $0
 ${If} $0 == ${BST_CHECKED}
  WriteRegDWORD ${REG_ROOT} "Software\${APP_KEY}" "AutoUpdateCheck" 1
 ${Else}
  WriteRegDWORD ${REG_ROOT} "Software\${APP_KEY}" "AutoUpdateCheck" 0
 ${EndIf}
FunctionEnd

Function .onInit
 ; Preselect the Windows display language when it is one of the five; any other language starts on
 ; English (NSIS would fall back to the first inserted language, Korean). 11-01 user.
 System::Call 'kernel32::GetUserDefaultUILanguage() i .r0'
 IntOp $0 $0 & 0x3FF
 ; LogicLib != is a string compare ("18" vs "0x12" always differs); <> compares numbers.
 ${If} $0 <> 0x12
 ${AndIf} $0 <> 0x11
 ${AndIf} $0 <> 0x04
  StrCpy $LANGUAGE ${LANG_ENGLISH}
 ${EndIf}
 !insertmacro MUI_LANGDLL_DISPLAY
 SetShellVarContext ${SHELL_CONTEXT}
 SetRegView 64
 ReadRegStr $0 ${REG_ROOT} "${REG_APP}" "InstallDir"
 StrCmp $0 "" init_dir_done
 IfFileExists "$0\Uninstall.exe" 0 init_dir_done
 StrCpy $INSTDIR $0
 init_dir_done:
 ${IfNot} ${RunningX64}
  MessageBox MB_ICONSTOP "$(MDNoteText7)" /SD IDOK
  Abort
 ${EndIf}
 ${IfNot} ${AtLeastWin10}
  MessageBox MB_ICONSTOP "$(MDNoteText8)" /SD IDOK
  Abort
 ${EndIf}
 ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
 ${If} $0 < 528040
  MessageBox MB_ICONSTOP "$(MDNoteText9)" /SD IDOK
  Abort
 ${EndIf}
FunctionEnd

Function ValidateDirectory
 !ifdef TEST_BUILD
  FileOpen $9 "$EXEDIR\installer-debug.txt" a
  FileSeek $9 0 END
  FileWrite $9 "Validate: [$INSTDIR]$\r$\n"
  FileClose $9
 !endif
 ; Never install into a nonempty unrelated folder, or split an existing install.
 StrCmp $INSTDIR "" bad_dir
 GetFullPathName $2 "$INSTDIR"
 StrCmp $2 "" no_normalize
 StrCpy $INSTDIR $2
 no_normalize:
 !ifdef TEST_BUILD
  FileOpen $9 "$EXEDIR\installer-debug.txt" a
  FileSeek $9 0 END
  FileWrite $9 "FullPath: [$INSTDIR]$\r$\n"
  FileClose $9
 !endif
 ReadRegStr $0 ${REG_ROOT} "${REG_APP}" "InstallDir"
 ${If} $0 != ""
 ${AndIf} $0 != $INSTDIR
  MessageBox MB_ICONEXCLAMATION "$(MDNoteText19)" /SD IDOK
  Abort
 ${EndIf}
 ReadINIStr $0 "$INSTDIR\install-info.ini" "MDNote" "AppId"
 StrCmp $0 "${APP_KEY}" directory_ok
 IfFileExists "$INSTDIR\*.*" 0 directory_ok
 ClearErrors
 FindFirst $0 $1 "$INSTDIR\*.*"
 IfErrors directory_ok
 loop_dir:
  StrCmp $1 "" empty_dir
  StrCmp $1 "." next_dir
  StrCmp $1 ".." next_dir
  FindClose $0
  Goto bad_dir
 next_dir:
  FindNext $0 $1
  IfErrors empty_dir
  Goto loop_dir
 empty_dir:
 FindClose $0
 directory_ok:
 Return
 bad_dir:
 MessageBox MB_ICONEXCLAMATION "$(MDNoteText20)" /SD IDOK
 Abort
FunctionEnd

!macro CheckClosed UN
Function ${UN}CheckClosed
 IfFileExists "$INSTDIR\MDNote.exe" 0 done_closed
 System::Call 'kernel32::CreateFileW(w "$INSTDIR\MDNote.exe", i 0x40000000, i 1, p 0, i 3, i 0, p 0) p.r0'
 IntCmp $0 -1 locked_closed
 System::Call 'kernel32::CloseHandle(p r0)'
 Goto done_closed
 locked_closed:
 IfSilent silent_closed
 MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$(MDNoteText10)" IDRETRY retry_closed
 Abort
 retry_closed:
 Call ${UN}CheckClosed
 Return
 silent_closed:
 SetErrorLevel 2
 Abort
 done_closed:
FunctionEnd
!macroend
!insertmacro CheckClosed ""
!insertmacro CheckClosed "un."

Section "MDNote" Main
 Call ValidateDirectory
 Call CheckClosed
 SetOutPath "$INSTDIR"
 ClearErrors
 File /r "${PAYLOAD}\app\*.*"
 ${If} ${Errors}
  MessageBox MB_ICONSTOP "$(MDNoteText11)" /SD IDOK
  SetErrorLevel 2
  Abort
 ${EndIf}
 !include "legacy-cleanup.nsh"
 WriteINIStr "$INSTDIR\install-info.ini" "MDNote" "AppId" "${APP_KEY}"
 ; The language picked on the first wizard page becomes the app's first-run language and font
 ; (Preferences.InstallerLanguage reads it when no per-user settings file exists yet).
 StrCpy $R9 "ko"
 ${If} $LANGUAGE == ${LANG_ENGLISH}
  StrCpy $R9 "en"
 ${ElseIf} $LANGUAGE == ${LANG_JAPANESE}
  StrCpy $R9 "ja"
 ${ElseIf} $LANGUAGE == ${LANG_SIMPCHINESE}
  StrCpy $R9 "zh-CN"
 ${ElseIf} $LANGUAGE == ${LANG_TRADCHINESE}
  StrCpy $R9 "zh-TW"
 ${EndIf}
 WriteINIStr "$INSTDIR\install-info.ini" "MDNote" "Language" "$R9"
 WriteUninstaller "$INSTDIR\Uninstall.exe"
 WriteRegStr ${REG_ROOT} "${REG_APP}" "InstallDir" "$INSTDIR"
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "DisplayName" "${APP_NAME}"
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "DisplayVersion" "${VERSION}"
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "Publisher" "MDNote"
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "InstallLocation" "$INSTDIR"
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "DisplayIcon" "$INSTDIR\MDNote.exe,0"
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
 WriteRegStr ${REG_ROOT} "${REG_UNINSTALL}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
 WriteRegDWORD ${REG_ROOT} "${REG_UNINSTALL}" "NoModify" 1
 WriteRegDWORD ${REG_ROOT} "${REG_UNINSTALL}" "NoRepair" 1
 ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
 WriteRegDWORD ${REG_ROOT} "${REG_UNINSTALL}" "EstimatedSize" $0
 !ifndef TEST_BUILD
  ; Upgrade from the earlier Korean display name; keep the same AppId and path.
  Delete "$SMPROGRAMS\MD 메모장\MD 메모장.lnk"
  Delete "$SMPROGRAMS\MD 메모장\제거.lnk"
  RMDir "$SMPROGRAMS\MD 메모장"
  DeleteRegValue ${REG_ROOT} "Software\RegisteredApplications" "MD 메모장"
  ; Interactive installation creates only the shortcuts selected on the finish page.
  IfSilent 0 +2
  Call CreateStartMenuShortcuts
 !endif
 WriteRegStr ${REG_ROOT} "Software\Classes\${APP_ID}" "" "Markdown 문서 (MDNote)"
 WriteRegStr ${REG_ROOT} "Software\Classes\${APP_ID}\DefaultIcon" "" '$\"$INSTDIR\MDNote.exe$\",0'
 WriteRegStr ${REG_ROOT} "Software\Classes\${APP_ID}\shell\open\command" "" '$\"$INSTDIR\MDNote.exe$\" $\"%1$\"'
 WriteRegStr ${REG_ROOT} "Software\Classes\.md\OpenWithProgids" "${APP_ID}" ""
 WriteRegStr ${REG_ROOT} "Software\Classes\.markdown\OpenWithProgids" "${APP_ID}" ""
 WriteRegStr ${REG_ROOT} "Software\Classes\.txt\OpenWithProgids" "${APP_ID}" ""
 WriteRegStr ${REG_ROOT} "Software\${APP_KEY}\Capabilities" "ApplicationName" "${APP_NAME}"
 WriteRegStr ${REG_ROOT} "Software\${APP_KEY}\Capabilities" "ApplicationDescription" "Markdown 파일 뷰어 및 편집기"
 WriteRegStr ${REG_ROOT} "Software\${APP_KEY}\Capabilities\FileAssociations" ".md" "${APP_ID}"
 WriteRegStr ${REG_ROOT} "Software\${APP_KEY}\Capabilities\FileAssociations" ".markdown" "${APP_ID}"
 WriteRegStr ${REG_ROOT} "Software\${APP_KEY}\Capabilities\FileAssociations" ".txt" "${APP_ID}"
 WriteRegStr ${REG_ROOT} "Software\RegisteredApplications" "${APP_NAME}" "Software\${APP_KEY}\Capabilities"
 System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
 StrCpy $0 "en"
 ${Switch} $LANGUAGE
  ${Case} 1042
   StrCpy $0 "ko"
   ${Break}
  ${Case} 1041
   StrCpy $0 "ja"
   ${Break}
  ${Case} 2052
   StrCpy $0 "zh-CN"
   ${Break}
  ${Case} 1028
   StrCpy $0 "zh-TW"
   ${Break}
 ${EndSwitch}
 WriteRegStr ${REG_ROOT} "Software\${APP_KEY}" "DefaultLanguage" $0
 ; The finish page's "업데이트 자동 확인" box writes the real choice; a silent install has no such page: a first
 ; silent install starts unchecked, a silent upgrade (winget) keeps the choice made at the earlier install.
 ${If} ${Silent}
  ClearErrors
  ReadRegDWORD $0 ${REG_ROOT} "Software\${APP_KEY}" "AutoUpdateCheck"
  ${If} ${Errors}
   WriteRegDWORD ${REG_ROOT} "Software\${APP_KEY}" "AutoUpdateCheck" 0
  ${EndIf}
 ${EndIf}
SectionEnd

Function CreateDesktopShortcut
 !ifndef TEST_BUILD
  CreateShortCut "$DESKTOP\MDNote.lnk" "$INSTDIR\MDNote.exe"
  WriteRegDWORD ${REG_ROOT} "${REG_APP}" "DesktopShortcut" 1
 !endif
FunctionEnd
Function CreateStartMenuShortcuts
 !ifndef TEST_BUILD
  CreateDirectory "$SMPROGRAMS\MDNote"
  CreateShortCut "$SMPROGRAMS\MDNote\MDNote.lnk" "$INSTDIR\MDNote.exe"
  CreateShortCut "$SMPROGRAMS\MDNote\제거.lnk" "$INSTDIR\Uninstall.exe"
 !endif
FunctionEnd

Function un.onInit
 SetShellVarContext ${SHELL_CONTEXT}
 SetRegView 64
 StrCpy $RemoveSettings 0
 ReadINIStr $0 "$INSTDIR\install-info.ini" "MDNote" "AppId"
 ${If} $0 != "${APP_KEY}"
  MessageBox MB_ICONSTOP "$(MDNoteText12)" /SD IDOK
  Abort
 ${EndIf}
FunctionEnd

Function un.SettingsPage
 nsDialogs::Create 1018
 Pop $0
 ${If} $0 == error
  Abort
 ${EndIf}
 !insertmacro MUI_HEADER_TEXT "$(MDNoteText13)" "$(MDNoteText14)"
 ${NSD_CreateLabel} 0 0 100% 32u "$(MDNoteText21)"
 Pop $0
 ${NSD_CreateCheckbox} 0 48u 100% 14u "$(MDNoteText15)"
 Pop $SettingsCheckbox
 ${NSD_CreateLabel} 0 76u 100% 36u "$(MDNoteText22)"
 Pop $0
 nsDialogs::Show
FunctionEnd
Function un.SettingsLeave
 ${NSD_GetState} $SettingsCheckbox $RemoveSettings
FunctionEnd

Section "Uninstall"
 Call un.CheckClosed
 ; Explicit compile-time manifest; never recursively remove a user-selected folder.
 !include "legacy-cleanup.nsh"
 !include "uninstall-files.nsh"
 Delete "$INSTDIR\install-info.ini"
 Delete "$INSTDIR\Uninstall.exe"
 RMDir "$INSTDIR"
 !ifndef TEST_BUILD
  ReadRegDWORD $0 ${REG_ROOT} "${REG_APP}" "DesktopShortcut"
  ${If} $0 == 1
   Delete "$DESKTOP\MDNote.lnk"
  ${EndIf}
  Delete "$SMPROGRAMS\MDNote\MDNote.lnk"
  Delete "$SMPROGRAMS\MDNote\제거.lnk"
  RMDir "$SMPROGRAMS\MDNote"
  ${If} $RemoveSettings == ${BST_CHECKED}
   SetShellVarContext current
   Delete "$LOCALAPPDATA\MDNote\settings.xml"
   RMDir "$LOCALAPPDATA\MDNote"
  ${EndIf}
 !endif
 DeleteRegKey ${REG_ROOT} "${REG_UNINSTALL}"
 DeleteRegKey ${REG_ROOT} "${REG_APP}"
 DeleteRegKey ${REG_ROOT} "Software\${APP_KEY}\Capabilities"
 DeleteRegValue ${REG_ROOT} "Software\${APP_KEY}" "DefaultLanguage"
 DeleteRegValue ${REG_ROOT} "Software\${APP_KEY}" "AutoUpdateCheck"
 DeleteRegKey /ifempty ${REG_ROOT} "Software\${APP_KEY}"
 DeleteRegKey ${REG_ROOT} "Software\Classes\${APP_ID}"
 DeleteRegValue ${REG_ROOT} "Software\Classes\.md\OpenWithProgids" "${APP_ID}"
 DeleteRegValue ${REG_ROOT} "Software\Classes\.markdown\OpenWithProgids" "${APP_ID}"
 DeleteRegValue ${REG_ROOT} "Software\Classes\.txt\OpenWithProgids" "${APP_ID}"
 DeleteRegKey /ifempty ${REG_ROOT} "Software\Classes\.md\OpenWithProgids"
 DeleteRegKey /ifempty ${REG_ROOT} "Software\Classes\.markdown\OpenWithProgids"
 DeleteRegKey /ifempty ${REG_ROOT} "Software\Classes\.txt\OpenWithProgids"
 DeleteRegKey /ifempty ${REG_ROOT} "Software\Classes\.md"
 DeleteRegKey /ifempty ${REG_ROOT} "Software\Classes\.markdown"
 DeleteRegValue ${REG_ROOT} "Software\RegisteredApplications" "${APP_NAME}"
 System::Call 'shell32::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
SectionEnd
