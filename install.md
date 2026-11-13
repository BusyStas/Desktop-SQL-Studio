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

## Note on the target version

The projects currently target `net8.0` / `net8.0-windows`. The .NET 10 SDK can build them, but running the app and the tests needs the .NET 8 runtime too:

https://dotnet.microsoft.com/en-us/download/dotnet/8.0

(Choose ".NET Desktop Runtime 8" for the app and ".NET Runtime 8" for the tests, or install the .NET 8 SDK, which includes both.)

## Build and run

```
build.bat
run.bat
```
