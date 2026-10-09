!include "MUI2.nsh"
!include "FileFunc.nsh"
!insertmacro GetParameters
!ifndef APP_VERSION
  !define APP_VERSION "0.0.0"
!endif
!ifndef SOURCE_DIR
  !error "SOURCE_DIR is required"
!endif
!ifndef OUT_FILE
  !define OUT_FILE "AudioTranscriber-setup.exe"
!endif
!ifndef DEST_SIZE_KB
  !define DEST_SIZE_KB "1"
!endif
Name "AudioTranscriber"
OutFile "${OUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\AudioTranscriber"
RequestExecutionLevel user
Unicode true
SetCompressor /SOLID lzma
BrandingText "AudioTranscriber ${APP_VERSION}"
!define MUI_ABORTWARNING
!ifndef ICON_FILE
  !define ICON_FILE "${SOURCE_DIR}\Assets\AppIcon.ico"
!endif
!define MUI_ICON "${ICON_FILE}"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

; Setup is 32-bit: plain powershell.exe is the WOW64 copy, which sees SysWOW64 as System32 and cannot read 64-bit process paths.
!macro FindPowerShell
  StrCpy $2 "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists $2 +2
  StrCpy $2 "powershell.exe"
!macroend
; No single quotes inside: NSIS ends a single-quoted argument at the first quote, which cut the old command short.
!define CLOSE_APP_SCRIPT '$target=Join-Path $env:LOCALAPPDATA Programs\AudioTranscriber; Get-Process AudioTranscriber.App -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep -Seconds 3; Get-Process AudioTranscriber.App -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | Stop-Process -Force'

Section "AudioTranscriber" SecMain
  SectionIn RO
  SetShellVarContext current
  Call CloseRunningApp
  RMDir /r "$INSTDIR\_old"
  CreateDirectory "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*"
  Call InstallVcRuntimeIfMissing
  CreateDirectory "$SMPROGRAMS\AudioTranscriber"
  CreateShortCut "$SMPROGRAMS\AudioTranscriber\AudioTranscriber.lnk" "$INSTDIR\AudioTranscriber.App.exe" "" "$INSTDIR\AudioTranscriber.App.exe" 0
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "DisplayName" "AudioTranscriber"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "DisplayIcon" "$INSTDIR\AudioTranscriber.App.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "Publisher" "throndir2"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "NoRepair" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber" "EstimatedSize" ${DEST_SIZE_KB}
SectionEnd

Section /o "Desktop shortcut" SecDesktop
  SetShellVarContext current
  CreateShortCut "$DESKTOP\AudioTranscriber.lnk" "$INSTDIR\AudioTranscriber.App.exe" "" "$INSTDIR\AudioTranscriber.App.exe" 0
SectionEnd

Function CloseRunningApp
  !insertmacro FindPowerShell
  nsExec::ExecToStack '"$2" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "${CLOSE_APP_SCRIPT}"'
  Pop $0
  Pop $1
FunctionEnd

Function InstallVcRuntimeIfMissing
  !insertmacro FindPowerShell
  InitPluginsDir
  File "/oname=$PLUGINSDIR\vc-runtime.ps1" "vc-runtime.ps1"
  nsExec::ExecToStack '"$2" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "$PLUGINSDIR\vc-runtime.ps1"'
  Pop $0
  Pop $1
  ; 1638 = same or newer runtime already installed; 3010/1641 = installed, restart pending.
  StrCmp $0 0 done
  StrCmp $0 1638 done
  StrCmp $0 3010 done
  StrCmp $0 1641 done
  IfSilent done
  MessageBox MB_ICONINFORMATION "The Microsoft Visual C++ runtime could not be installed automatically, or its installer needs attention. AudioTranscriber will check again on startup."
  done:
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Call un.CloseRunningApp
  Delete "$DESKTOP\AudioTranscriber.lnk"
  Delete "$SMPROGRAMS\AudioTranscriber\AudioTranscriber.lnk"
  RMDir "$SMPROGRAMS\AudioTranscriber"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioTranscriber"
  RMDir /r "$INSTDIR"
SectionEnd

Function un.CloseRunningApp
  !insertmacro FindPowerShell
  nsExec::ExecToStack '"$2" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "${CLOSE_APP_SCRIPT}"'
  Pop $0
  Pop $1
FunctionEnd


