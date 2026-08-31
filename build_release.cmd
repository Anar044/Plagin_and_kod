@echo off

git add .
git commit -a -m "build to nuget" 
git push

set mydate=%date:~8,2%%date:~3,2%%date:~0,2%
set archive=hc_%mydate%_%1-prod
del /f hc_*_*-prod.zip
rmdir /s /q .\Resto.Front.Api.HorecaControlPlugin\bin\Release\
dotnet build -p:APIVersion=V8P5 --configuration Release
del /q /f .\Resto.Front.Api.HorecaControlPlugin\bin\Release\net472\Resto.Front.Api.HorecaControlPlugin.pdb
tar -a -c -f ".\%archive%.zip" -C ".\Resto.Front.Api.HorecaControlPlugin\bin\Release\net472" *
