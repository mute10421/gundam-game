@echo off
setlocal
cd /d "%~dp0"
set ROOT=%~dp0..

if not exist "%ROOT%\Assets\Models\Zaku" mkdir "%ROOT%\Assets\Models\Zaku"

echo Reassembling Zaku FBX from parts...
copy /b zaku_fbx_00.part+zaku_fbx_01.part+zaku_fbx_02.part+zaku_fbx_03.part+zaku_fbx_04.part "%ROOT%\Assets\Models\Zaku\Green Zaku Mobile Suit-aca4bc150af72115520493e85c7e2457.fbx"
if errorlevel 1 goto :error

echo Reassembling Zaku texture from parts...
copy /b zaku_tex_00.part+zaku_tex_01.part "%ROOT%\Assets\Models\Zaku\ZakuTexture.png"
if errorlevel 1 goto :error

echo Reassembling Gundam base color texture from parts...
copy /b gundam_tex_00.part+gundam_tex_01.part "%ROOT%\Assets\Models\Gundam\Mobile Suit Gundam-baseColor.png"
if errorlevel 1 goto :error

echo Done. Files reassembled into Assets\Models\Zaku\ and Assets\Models\Gundam\
echo Cleaning up part files...
del zaku_fbx_*.part
del zaku_tex_*.part
del gundam_tex_*.part
echo You can now delete this _ZakuImport folder if you want.
goto :eof

:error
echo FAILED - one of the copy steps returned an error. The .part files were left in place - do not delete them, tell Claude what happened.
