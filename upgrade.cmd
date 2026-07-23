@echo off



if not exist listsln.txt (dir /b /A /s "*.sln*" > listsln.txt)

for /f "tokens=*" %%G in (listsln.txt) DO (
 Pushd %%~dpG
 @echo %%G
 dotnet restore
 dotnet build
 dotnet-outdated --upgrade
 dotnet restore
 dotnet build
 
 Popd )


pause