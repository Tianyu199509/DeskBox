@ECHO OFF
REM NativeAOT x64 publish helper: BuildTools' default VC toolset (14.42) is
REM missing the x64 static CRT (LIBCMT.lib), while 14.44 has it. Pin the
REM vcvars environment to 14.44 and let ILCompiler use the environmental
REM tools instead of its own vswhere probe.
CALL "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" amd64 -vcvars_ver=14.44 || EXIT /B 1
dotnet publish src\DeskBox\DeskBox.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 -p:DeskBoxAotAudit=true -p:DeskBoxRustNative=true -p:IlcUseEnvironmentalTools=true -v minimal
