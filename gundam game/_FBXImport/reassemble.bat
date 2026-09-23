@echo off
cd /d "%~dp0"
echo Reassembling Gundam FBX from parts...
copy /b part_00+part_01+part_02+part_03 "..\Assets\Models\Gundam\Mobile Suit Gundam-52dddca0b3f82f4705b1562457f679a8.fbx"
if exist "..\Assets\Models\Gundam\Mobile Suit Gundam-52dddca0b3f82f4705b1562457f679a8.fbx" (
    echo Done. FBX reassembled into Assets\Models\Gundam\
    del part_00 part_01 part_02 part_03
    echo You can now delete this _FBXImport folder if you want.
) else (
    echo ERROR: reassembly failed - one of the part files may be missing.
)
pause
