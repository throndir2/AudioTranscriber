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
  nsExec::ExecToStack 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "$target=$env:LOCALAPPDATA + ''\Programs\AudioTranscriber''; Get-Process AudioTranscriber.App -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep -Seconds 3; Get-Process AudioTranscriber.App -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | Stop-Process -Force"'
  Pop $0
  Pop $1
FunctionEnd

Function InstallVcRuntimeIfMissing
  nsExec::ExecToStack 'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$files=''msvcp140.dll'',''vcruntime140.dll'',''vcruntime140_1.dll'',''vcomp140.dll''; $ok=$true; foreach($f in $files){ if(-not(Test-Path (Join-Path $env:WINDIR (''System32\''+$f)))){ $ok=$false } }; if($ok){ exit 0 }; $dir=Join-Path $env:TEMP ''AudioTranscriber''; New-Item -ItemType Directory -Force $dir | Out-Null; $out=Join-Path $dir ''vc_redist.x64.exe''; try { Invoke-WebRequest -Uri ''https://aka.ms/vs/17/release/vc_redist.x64.exe'' -OutFile $out -UseBasicParsing; $p=Start-Process -FilePath $out -ArgumentList ''/install'',''/passive'',''/norestart'' -Wait -PassThru; exit $p.ExitCode } catch { exit 99 }"'
  Pop $0
  Pop $1
  StrCmp $0 0 done
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
  nsExec::ExecToStack 'powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command "$target=$env:LOCALAPPDATA + ''\Programs\AudioTranscriber''; Get-Process AudioTranscriber.App -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep -Seconds 3; Get-Process AudioTranscriber.App -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | Stop-Process -Force"'
  Pop $0
  Pop $1
FunctionEnd


