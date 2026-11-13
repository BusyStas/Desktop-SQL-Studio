# Install

## Requirements

- Windows 10 or 11
- .NET SDK (includes the runtime and the Windows desktop/WPF support)

## Install the .NET SDK

Download the **.NET 10 SDK** (the current LTS release) for Windows x64:

https://dotnet.microsoft.com/en-us/download/dotnet/10.0

Or with winget:

```
winget install Microsoft.DotNet.SDK.10
```

All downloads and other versions: https://dotnet.microsoft.com/en-us/download/dotnet

Check the install:

```
dotnet --version
```

## Target version

All projects target .NET 10 (`net10.0` / `net10.0-windows`), so the .NET 10 SDK is the only thing you need.

## Build and run

```
build.bat
run.bat
```
