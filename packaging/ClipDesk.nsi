Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
Name "ClipDesk ${APP_VERSION}"
OutFile "${PROJECT_ROOT}\artifacts\ClipDesk-${APP_VERSION}-win-x64-setup.exe"
InstallDir "$LOCALAPPDATA\Programs\ClipDesk"
InstallDirRegKey HKCU "Software\ClipDesk" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID zlib
ShowInstDetails show
ShowUninstDetails show
BrandingText "ClipDesk · 个人桌面工具"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "ClipDesk"
VIAddVersionKey /LANG=2052 "FileDescription" "ClipDesk 中文安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "ClipDesk 个人桌面工具"
!define MUI_ICON "${PROJECT_ROOT}\src\ClipDesk.App\Assets\Brand\App.ico"
!define MUI_UNICON "${PROJECT_ROOT}\src\ClipDesk.App\Assets\Brand\App.ico"
!define MUI_WELCOMEPAGE_TEXT "安装 ClipDesk 剪贴、待办与备忘工具。$\r$\n$\r$\n更新前请从托盘退出 ClipDesk，等待草稿保存完成。更新和卸载都保留用户数据。$\r$\n$\r$\n支持独立置顶、待办与备忘窗口，各自设置显示方式、透明度和快捷键。$\r$\n验收范围详见随程序附带的《验收记录》。"
!define MUI_FINISHPAGE_RUN "$INSTDIR\ClipDesk.exe"
!define MUI_FINISHPAGE_RUN_TEXT "打开 ClipDesk"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

!macro CHECK_RUNNING
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\ClipDesk.InstallGuard") p.r0'
  ${If} $0 P<> 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "ClipDesk 仍在运行。请从托盘选择“退出 ClipDesk”，等待草稿保存完成后再重试。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "此安装包需要 64 位 Windows。" /SD IDOK
    SetErrorLevel 2
    Abort
  ${EndIf}
  SetShellVarContext current
  SetRegView 64
  ${If} $INSTDIR == "$LOCALAPPDATA\Programs\ClipDesk"
    IfFileExists "D:\APP\*.*" 0 +2
      StrCpy $INSTDIR "D:\APP\ClipDesk"
  ${EndIf}
  !insertmacro CHECK_RUNNING
FunctionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  !insertmacro CHECK_RUNNING
FunctionEnd

Section "安装 ClipDesk"
  !insertmacro CHECK_RUNNING
  SetOutPath "$INSTDIR"
  SetOverwrite on
  ClearErrors
  File /r /x *.pdb "${PROJECT_ROOT}\artifacts\ClipDesk\*.*"
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "程序文件未全部写入。请检查安装目录权限和可用空间，再重新安装。用户数据没有被删除。" /SD IDOK
    SetErrorLevel 3
    Abort
  ${EndIf}
  ClearErrors
  WriteUninstaller "$INSTDIR\卸载 ClipDesk.exe"
  WriteRegStr HKCU "Software\ClipDesk" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "DisplayName" "ClipDesk"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "DisplayIcon" "$INSTDIR\ClipDesk.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "UninstallString" '"$INSTDIR\卸载 ClipDesk.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "NoRepair" 1
  ${If} $SMPROGRAMS != ""
    CreateDirectory "$SMPROGRAMS\ClipDesk"
    CreateShortcut "$SMPROGRAMS\ClipDesk\ClipDesk.lnk" "$INSTDIR\ClipDesk.exe"
  ${Else}
    SetErrors
  ${EndIf}
  ${If} ${Errors}
    MessageBox MB_OK|MB_ICONSTOP "程序文件已写入，但当前账户无法完整创建卸载入口或开始菜单。请在正常登录的 Windows 桌面重新运行安装器。用户数据没有被删除。" /SD IDOK
    SetErrorLevel 4
    Abort
  ${EndIf}
  DetailPrint "安装完成。用户数据保存在 %LOCALAPPDATA%\ClipDesk。"
SectionEnd

Section "Uninstall"
  !insertmacro CHECK_RUNNING
  !include "${UNINSTALL_LIST}"
  Delete "$INSTDIR\卸载 ClipDesk.exe"
  RMDir "$INSTDIR"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ClipDesk"
  ${If} $0 == '"$INSTDIR\ClipDesk.exe" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "ClipDesk"
  ${EndIf}
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk" "InstallLocation"
  ${If} $0 == $INSTDIR
    ${If} $SMPROGRAMS != ""
      Delete "$SMPROGRAMS\ClipDesk\ClipDesk.lnk"
      RMDir "$SMPROGRAMS\ClipDesk"
    ${EndIf}
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipDesk"
    DeleteRegKey HKCU "Software\ClipDesk"
  ${EndIf}
  DetailPrint "程序已卸载。剪贴、待办、备忘、草稿和设置均已保留。"
SectionEnd
