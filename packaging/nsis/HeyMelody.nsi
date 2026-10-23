; Compiled only by Build-Installer.ps1, independently of the ZIP/Actions pipeline.
Unicode true
!include "${BUILD_CONFIG}"
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\HeyMelody"
Name "HeyMelody ${APP_VERSION} (${APP_ARCH})"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\HeyMelody"
InstallDirRegKey HKCU "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 32
ShowInstDetails show
ShowUninstDetails show
AllowSkipFiles off
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName" "HeyMelody for Windows"
VIAddVersionKey /LANG=2052 "FileDescription" "HeyMelody 当前用户安装程序"
VIAddVersionKey /LANG=2052 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "HeyMelody contributors; third-party rights retained"

!define MUI_ICON "${APP_ICON}"
!define MUI_UNICON "${APP_ICON}"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "安装 HeyMelody"
!define MUI_WELCOMEPAGE_TEXT "将 HeyMelody 安装到当前用户目录，无需管理员权限。下一步可选择安装位置。$\r$\n$\r$\n更新前请关闭所有 HeyMelody 窗口，并通过托盘菜单退出后台。$\r$\n$\r$\n旧款 Qualcomm Adreno 680/685/690（QCOM043A）设备将自动启用 Basic Render 兼容模式。其他设备保持原设置。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_ARCH}\HeyMelody.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!define MUI_FINISHPAGE_RUN_TEXT "打开 HeyMelody"
!define MUI_FINISHPAGE_SHOWREADME
!define MUI_FINISHPAGE_SHOWREADME_TEXT "创建桌面快捷方式"
!define MUI_FINISHPAGE_SHOWREADME_FUNCTION CreateDesktopShortcut
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"

Var ExistingInstall
Var HelperResult
Var HelperOutput
Var SetupMutex
Var UninstallMutex

!macro AcquireMutex Name
    System::Call 'kernel32::CreateMutexW(p0, i0, w "Local\HeyMelody.${Name}") p.r0 ?e'
    Pop $1
    ${If} $1 == 183
    ${OrIf} $0 == 0
        MessageBox MB_ICONSTOP|MB_OK "另一个 HeyMelody 安装或卸载程序正在运行，请稍后重试。" /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
!macroend

!macro PrepareHelper
    InitPluginsDir
    SetOutPath "$PLUGINSDIR"
    File /oname=HeyMelody.InstallerSupport.exe "${HELPER_EXE}"
!macroend

!macro RequireNoRunning Directory
    nsExec::ExecToStack /TIMEOUT=30000 '"$PLUGINSDIR\HeyMelody.InstallerSupport.exe" check-running "${Directory}"'
    Pop $HelperResult
    Pop $HelperOutput
    ${If} $HelperResult != 0
        MessageBox MB_ICONSTOP|MB_OK "请关闭所有 HeyMelody 窗口，并通过托盘菜单退出后台后重试。$\r$\n$\r$\n$HelperOutput" /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
!macroend

Function .onInit
    SetShellVarContext current
    ; A single 32-bit HKCU registration is shared across package architectures.
    SetRegView 32
    !insertmacro AcquireMutex "Setup"
    StrCpy $SetupMutex $0
    System::Call 'kernel32::OpenMutexW(i 0x100000, i0, w "Local\HeyMelody.Uninstall") p.r0'
    ${If} $0 != 0
        System::Call 'kernel32::CloseHandle(p r0)'
        SetErrorLevel 1
        Abort "另一个 HeyMelody 卸载程序正在运行。"
    ${EndIf}
    ${IfNot} ${AtLeastWin10}
        MessageBox MB_ICONSTOP|MB_OK "需要 Windows 10 2004（19041）或更新版本。" /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
    ${If} $0 < 19041
        MessageBox MB_ICONSTOP|MB_OK "需要 Windows 10 2004（19041）或更新版本。" /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
!if "${APP_ARCH}" == "arm64"
    ${IfNot} ${IsNativeARM64}
!else
    ${IfNot} ${IsNativeAMD64}
!endif
        MessageBox MB_ICONSTOP|MB_OK "此安装包适用于 Windows ${APP_ARCH}，请选择与系统架构对应的安装包。" /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    !insertmacro PrepareHelper
    ; Read-only diagnostic used by the acceptance script before it can alter
    ; a real user's preferences on affected hardware.
    ${GetParameters} $0
    ClearErrors
    ${GetOptions} $0 "/CHECKCOMPATIBILITY" $1
    ${IfNot} ${Errors}
        nsExec::ExecToStack /TIMEOUT=30000 '"$PLUGINSDIR\HeyMelody.InstallerSupport.exe" probe-compatibility'
        Pop $HelperResult
        Pop $HelperOutput
        ${If} $HelperResult == 0
            SetErrorLevel 0
        ${ElseIf} $HelperResult == 20
            SetErrorLevel 20
        ${Else}
            SetErrorLevel 1
        ${EndIf}
        Quit
    ${EndIf}
FunctionEnd

Function CreateDesktopShortcut
    SetShellVarContext current
    SetOutPath "$INSTDIR\${APP_ARCH}"
    ClearErrors
    CreateShortcut "$DESKTOP\HeyMelody.lnk" "$INSTDIR\${APP_ARCH}\HeyMelody.exe"
    ${If} ${Errors}
        MessageBox MB_ICONEXCLAMATION|MB_OK "无法创建桌面快捷方式，可通过开始菜单打开 HeyMelody。" /SD IDOK
        SetErrorLevel 1
    ${Else}
        WriteRegDWORD HKCU "${UNINSTALL_KEY}" "DesktopShortcut" 1
    ${EndIf}
FunctionEnd

Section "HeyMelody" Main
    !insertmacro RequireNoRunning "$INSTDIR"
    ReadRegStr $ExistingInstall HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $ExistingInstall != ""
        IfFileExists "$ExistingInstall\Uninstall.exe" 0 old_uninstaller_missing
        DetailPrint "卸载旧程序文件（保留用户设置和缓存）..."
        ClearErrors
        ExecWait '"$ExistingInstall\Uninstall.exe" /S /FROMINSTALLER _?=$ExistingInstall' $HelperResult
        ${If} ${Errors}
            MessageBox MB_ICONSTOP|MB_OK "无法启动旧版卸载程序，请检查安装目录权限后重试。" /SD IDOK
            SetErrorLevel 1
            Abort
        ${EndIf}
        ${If} $HelperResult != 0
            MessageBox MB_ICONSTOP|MB_OK "无法更新。请关闭所有 HeyMelody 窗口，并通过托盘菜单退出后台后重试。" /SD IDOK
            SetErrorLevel 1
            Abort
        ${EndIf}
        Delete "$ExistingInstall\Uninstall.exe"
        RMDir "$ExistingInstall"
        Goto old_uninstaller_done
old_uninstaller_missing:
        MessageBox MB_ICONSTOP|MB_OK "旧版卸载程序缺失，请先修复或移除旧版安装后重试。" /SD IDOK
        SetErrorLevel 1
        Abort
old_uninstaller_done:
    ${EndIf}
    SetOverwrite on
    ClearErrors
    !include "${GENERATED_DIR}\install-files.nsh"
    ${If} ${Errors}
        SetErrorLevel 1
        Abort "程序文件未能完整安装，请检查目录权限后重试。"
    ${EndIf}
    SetOutPath "$INSTDIR"
    ClearErrors
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    ${If} ${Errors}
        SetErrorLevel 1
        Abort "无法创建卸载程序，请检查目录权限后重试。"
    ${EndIf}
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "HeyMelody for Windows"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "HeyMelody contributors"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_ARCH}\HeyMelody.exe,0"
    WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
    WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/swift-lee/heymelody-windows"
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
    WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" ${ESTIMATED_SIZE}
    ${If} ${Errors}
        SetErrorLevel 1
        Abort "无法注册当前用户的卸载项，请检查权限后重试。"
    ${EndIf}
    CreateDirectory "$SMPROGRAMS\HeyMelody"
    SetOutPath "$INSTDIR\${APP_ARCH}"
    CreateShortcut "$SMPROGRAMS\HeyMelody\HeyMelody.lnk" "$INSTDIR\${APP_ARCH}\HeyMelody.exe"
    CreateShortcut "$SMPROGRAMS\HeyMelody\设备管理器.lnk" "$INSTDIR\${APP_ARCH}\HeyMelody.DeviceManager.exe"
    CreateShortcut "$SMPROGRAMS\HeyMelody\离线 3D 演示.lnk" "$INSTDIR\${APP_ARCH}\HeyMelody.exe" "--demo"
    CreateShortcut "$SMPROGRAMS\HeyMelody\卸载 HeyMelody.lnk" "$INSTDIR\Uninstall.exe"
    ${If} ${Errors}
        SetErrorLevel 1
        Abort "程序文件已安装，但无法创建当前用户的开始菜单快捷方式。"
    ${EndIf}
    DetailPrint "检查旧款 Qualcomm GPU 的 Basic Render 兼容默认值..."
    nsExec::ExecToStack /TIMEOUT=30000 '"$PLUGINSDIR\HeyMelody.InstallerSupport.exe" compatibility'
    Pop $HelperResult
    Pop $HelperOutput
    DetailPrint "$HelperOutput"
    ${If} $HelperResult != 0
        MessageBox MB_ICONEXCLAMATION|MB_OK "程序文件已安装，但无法完成兼容模式设置。原有设置未被清空。$\r$\n$\r$\n$HelperOutput$\r$\n$\r$\n请查看安装详情或手动设置 Basic Render 后重新打开界面。" /SD IDOK
        SetErrorLevel 1
    ${EndIf}
SectionEnd

Function un.onInit
    SetShellVarContext current
    SetRegView 32
    !insertmacro AcquireMutex "Uninstall"
    StrCpy $UninstallMutex $0
    ${GetParameters} $0
    ClearErrors
    ${GetOptions} $0 "/FROMINSTALLER" $1
    ${If} ${Errors}
        System::Call 'kernel32::OpenMutexW(i 0x100000, i0, w "Local\HeyMelody.Setup") p.r0'
        ${If} $0 != 0
            System::Call 'kernel32::CloseHandle(p r0)'
            SetErrorLevel 1
            Abort "另一个 HeyMelody 安装程序正在运行。"
        ${EndIf}
    ${EndIf}
    !insertmacro PrepareHelper
    !insertmacro RequireNoRunning "$INSTDIR"
FunctionEnd

Section "Uninstall"
    SetOutPath "$TEMP"
    ClearErrors
    ; Delete only the exact files shipped by this installer, never a recursive directory tree.
    !include "${GENERATED_DIR}\uninstall-files.nsh"
    ${If} ${Errors}
        MessageBox MB_ICONSTOP|MB_OK "部分程序文件无法移除，请关闭 HeyMelody 后重试。用户设置与缓存已保留。" /SD IDOK
        SetErrorLevel 1
        Abort
    ${EndIf}
    !include "${GENERATED_DIR}\uninstall-directories.nsh"
    ReadRegStr $0 HKCU "${UNINSTALL_KEY}" "InstallLocation"
    ${If} $0 == $INSTDIR
        ReadRegDWORD $1 HKCU "${UNINSTALL_KEY}" "DesktopShortcut"
        ${If} $1 == 1
            Delete "$DESKTOP\HeyMelody.lnk"
        ${EndIf}
        Delete "$SMPROGRAMS\HeyMelody\HeyMelody.lnk"
        Delete "$SMPROGRAMS\HeyMelody\设备管理器.lnk"
        Delete "$SMPROGRAMS\HeyMelody\离线 3D 演示.lnk"
        Delete "$SMPROGRAMS\HeyMelody\卸载 HeyMelody.lnk"
        RMDir "$SMPROGRAMS\HeyMelody"
        DeleteRegKey HKCU "${UNINSTALL_KEY}"
    ${EndIf}
    Delete "$INSTDIR\Uninstall.exe"
    RMDir "$INSTDIR"
SectionEnd
