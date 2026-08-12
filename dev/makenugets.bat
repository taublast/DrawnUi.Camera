@echo off
 

dotnet pack  ..\src\NET\Lib\SkiaCamera.Net.csproj -c Release
dotnet pack  ..\src\MAUI\Lib\DrawnUi.Maui.Camera.csproj -c Release



pause
