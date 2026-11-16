@ECHO OFF
D:/upx/upx.exe --best --lzma --compress-icons=0 --compress-resources=0 --strip-relocs=0 "build/ScriptLangCore.Launcher.exe"
Pause