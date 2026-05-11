@echo off
setlocal
pushd "%~dp0.."
dotnet run --project "src\ClaudeMigrator.App\ClaudeMigrator.App.csproj"
if errorlevel 1 (
    echo.
    echo Claude Migrator exited with an error.
    pause
)
popd
endlocal
