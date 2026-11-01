# MDNote 설치 패키지

NSIS 3.13 Modern UI 2를 사용한다. 언어 선택(한국어/영어/일본어/중국어 간체/번체) → 안내 → 설치 경로 → 설치 → 완료 순서이며, 관리자 권한으로 모든 사용자용으로 설치한다. 기본 위치는 `$PROGRAMFILES64\MDNote` (일반적인 PC에서 `C:\Program Files\MDNote`)이며 설치 중 변경할 수 있다. 프로그램은 공용이고 화면 설정은 각 사용자 계정별이다.

빌드 예:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\build-setup.ps1 -NsisDirectory C:\Tools\nsis-3.13
```

이 명령은 부모 폴더에 `MDNote-Setup-2.0.exe`를 만든다. 설치 폴더에는 실행 파일·AvalonEdit DLL·LICENSE·THIRD_PARTY_NOTICES.txt만, 도움말 문서와 그림은 `help` 폴더에 설치한다(payload의 `app` 폴더). 소스와 빌드 스크립트는 설치하지 않는다(소스는 GitHub). `legacy-files.txt`에 다음 판에서 빠지거나 이름이 바뀌는 파일을 적으면 업데이트·제거 때 지운다(`\`로 끝나는 줄은 빈 폴더 제거). 2.0 기준으로는 비어 있다. 설치 소스를 변경하면 설치 EXE를 다시 빌드해야 한다.

도구: [NSIS 공식 다운로드](https://nsis.sourceforge.io/Download), [Modern UI 2 문서](https://nsis.sourceforge.io/Docs/Modern%20UI%202/Readme.html). 포터블 ZIP을 작업 도구 폴더에 풀어 사용하면 NSIS 자체를 Windows에 설치할 필요가 없다. 이번에 사용한 공식 ZIP의 SHA-256은 `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`이다.

## 등록 및 제거

- 시스템 공용 HKLM 64비트 뷰의 `Software\Microsoft\Windows\CurrentVersion\Uninstall\MDNote`에 앱 이름·버전·제거 명령을 등록한다. Windows 설정의 설치된 앱과 제어판 프로그램 및 기능에서 제거할 수 있다.
- 완료 화면에서 바탕화면/시작 메뉴 바로가기를 각각 선택한다. 기본은 바탕화면 선택, 시작 메뉴 해제이다. 마침을 누르면 선택한 바로가기를 만든다. 조용한 /S 설치는 시작 메뉴 바로가기를 만든다. 재설치 시 기존 바로가기는 선택 해제만으로 지우지 않는다.
- `MDNote.Document` ProgID, `.md/.markdown` OpenWithProgids 및 RegisteredApplications/Capabilities를 등록한다. 기존 UserChoice와 기본 앱은 바꾸지 않는다.
- 제거는 빌드 시 생성한 `uninstall-files.nsh`의 설치 파일만 삭제한다. 설치 루트에 재귀 삭제를 쓰지 않는다. 사용자가 추가한 문서는 보존하며 폴더가 비어 있을 때만 제거한다.
- 제거 화면에서 설정 삭제를 체크하면 제거를 실행한 계정의 `%LocalAppData%\MDNote\settings.xml`만 추가 삭제한다. 기본은 설정 유지다. 다른 계정의 사용자 설정은 삭제하지 않는다. 다른 파일이 있으면 설정 폴더도 남긴다.
- 프로그램이 실행 중이면 문서를 저장하고 닫도록 요청한다. 강제 종료하지 않는다.
- 전용 빈 폴더 또는 설치 마커가 있는 기존 위치만 허용한다. 기존 설치가 있을 때 다른 경로에 덮어 등록하지 않는다. 경로 이동은 기존 제거 후 재설치한다.
- 설치 EXE와 앱은 디지털 서명되지 않았다. 보안 예외나 신뢰 인증서를 설치하지 않는다.

## 시험 빌드

`-TestBuild -OutputFile <시험 EXE 경로>`는 독립 앱 이름과 `MDNoteInstallerSmokeTest` 레지스트리/ProgID를 쓴다. 실제 MDNote 등록 및 설정을 덮지 않으며 시작 메뉴 바로가기도 생략한다. Windows 기본 앱 설정은 변경하지 않는다. `/S /D=<전용 시험 폴더>`로 조용히 설치하며 `/D=`는 반드시 마지막 인수다. 제거 EXE의 `/S`는 설정을 유지한다.

설치 마법사의 안내/경로/선택 화면은 NSIS 표준 UI를 고른 언어로 보여 준다. 회귀 검증에는 독립 시험 빌드의 조용한 설치·동일 경로 재설치·레지스트리 확인·사용자 파일 보존·제거를 사용한다. 실제 프린터 작업은 보내지 않는다.

새 폴더에는 GetFullPathName이 빈 문자열을 반환할 수 있으므로 반환값이 있을 때만 INSTDIR에 반영한다. 존재하지 않는 경로는 정상적인 새 설치 대상으로 허용한다. 레지스트리의 기존 설치 경로는 .onInit의 SetRegView 64 이후에 다시 읽는다.

1.9.1: legacy-files.txt의 이전 검사 자료만 정확한 파일명으로 정리하며, 검사 폴더에 사용자 문서가 남으면 폴더도 유지한다. install-notice.txt는 한국어 설치 안내이다. 빌드용 임시 패키지 폴더는 컴파일 후 제거한다. test-setup.ps1에 패키지 제외 및 업그레이드 잔여물 정리/사용자 문서 보존 검사가 추가되었다.

2.0: 설치 안내문은 `install-notice.txt`(한국어)와 언어별 `install-notice.<언어>.txt`, 설치 화면 문구는 `languages.nsh`에 있다. 첫 화면 언어는 Windows 표시 언어로 미리 고른다. AvalonEdit 실행 DLL과 lib DLL, 수정 소스 및 MIT 라이선스를 포함하고 bin/obj 빌드 산출물은 제외한다.
