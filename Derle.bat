@echo off
setlocal
cd /d "%~dp0"
title Plaka Takip - EXE olusturucu
echo ============================================
echo   Plaka Takip - EXE olusturucu
echo ============================================
echo.

dotnet --list-sdks 2>nul | findstr /R "[0-9]" >nul
if %errorlevel%==0 goto have_dotnet

echo .NET SDK bulunamadi.
where winget >nul 2>nul
if %errorlevel%==0 goto try_winget
echo.
echo Lutfen su adresten ".NET SDK 8.0" yukleyin:
echo   https://dotnet.microsoft.com/download/dotnet/8.0
echo Kurulumdan sonra bu dosyayi tekrar calistirin.
pause
exit /b 1

:try_winget
echo .NET 8 SDK winget ile kuruluyor, lutfen bekleyin...
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
echo.
echo Kurulum tamamlandi. Bu pencereyi kapatip Derle.bat dosyasini TEKRAR calistirin.
pause
exit /b 0

:have_dotnet
if exist "tessdata\eng.traineddata" goto have_tess
echo OCR dil dosyasi indiriliyor...
powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata' -OutFile 'tessdata\eng.traineddata'"
if exist "tessdata\eng.traineddata" goto have_tess
echo.
echo HATA: eng.traineddata indirilemedi. Internet baglantinizi kontrol edin
echo veya dosyayi elle tessdata klasorune koyun.
pause
exit /b 1

:have_tess
echo.
echo EXE derleniyor. Ilk seferde paketler indirilecek, birkac dakika surebilir...
echo.
dotnet publish PlakaTakip.csproj -c Release -r win-x64 --self-contained true -o publish
if %errorlevel%==0 goto built
echo.
echo HATA: derleme basarisiz oldu. Yukaridaki hata mesajini kopyalayip bana gonderin.
pause
exit /b 1

:built
if not exist "publish\tessdata" mkdir "publish\tessdata"
copy /Y "tessdata\eng.traineddata" "publish\tessdata\" >nul
echo.
echo TAMAM! Program: %~dp0publish\PlakaTakip.exe
start "" "%~dp0publish\PlakaTakip.exe"
pause
