@echo off

git add .
git commit -a -m "build to nuget" 
git push

set mydate=%date:~8,2%%date:~3,2%%date:~0,2%
set archive=hc_%mydate%_%1-dbg
del /f hc_*_*-dbg.zip
rmdir /s /q .\Resto.Front.Api.HorecaControlPlugin\bin\Debug\
dotnet build -p:APIVersion=V8P5 --configuration Debug
del /q /f .\Resto.Front.Api.HorecaControlPlugin\bin\Debug\net472\Resto.Front.Api.HorecaControlPlugin.pdb
tar -a -c -f ".\%archive%.zip" -C ".\Resto.Front.Api.HorecaControlPlugin\bin\Debug\net472" *
