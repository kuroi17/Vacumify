# Vacumify (Storage Cleaner)

A lightweight, local-first Windows WPF utility for discovering and safely cleaning forgotten, duplicate, and large files.

## How to Run

Requires .NET 10 SDK on Windows:

```powershell
dotnet run --project .\StorageCleaner\StorageCleaner.csproj
```

To build a standalone executable:
```powershell
dotnet publish .\StorageCleaner\StorageCleaner.csproj -c Release -r win-x64 --self-contained true
```
The output executable is named `Vacumify.exe`.
