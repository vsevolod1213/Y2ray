# Yvpn Desktop

Simplified desktop VPN client for Windows, macOS, Linux, based on the `v2rayN` engine.

## What was changed

- Branded app identity: `Yvpn`.
- Simplified main UI: one central `CONNECT` / `DISCONNECT` button.
- New config defaults for fresh installs: tunnel mode + system proxy ready for quick start.
- Quick onboarding action: import server/subscription link from clipboard.

## Prerequisites

- .NET 8 SDK (required to build/run this repo).
- Git submodules initialized.

## Prepare workspace

```powershell
cd v2rayN
git submodule update --init --recursive
dotnet --info
```

`dotnet --info` must show an installed **SDK 8.x** (runtime only is not enough).

## Run locally (development)

```powershell
dotnet restore .\v2rayN.Desktop\v2rayN.Desktop.csproj
dotnet run --project .\v2rayN.Desktop\v2rayN.Desktop.csproj
```

## Build release binaries

Windows:

```powershell
dotnet publish .\v2rayN.Desktop\v2rayN.Desktop.csproj -c Release -r win-x64 -p:SelfContained=true -o .\Release\windows-64
dotnet publish .\v2rayN.Desktop\v2rayN.Desktop.csproj -c Release -r win-arm64 -p:SelfContained=true -o .\Release\windows-arm64
```

Linux:

```bash
dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r linux-x64 -p:SelfContained=true -o ./Release/linux-64
dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r linux-arm64 -p:SelfContained=true -o ./Release/linux-arm64
```

macOS:

```bash
dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r osx-x64 -p:SelfContained=true -o ./Release/macos-64
dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r osx-arm64 -p:SelfContained=true -o ./Release/macos-arm64
```

## How to test Yvpn client

1. Start app with admin/root privileges (required for full TUN behavior).
2. Copy your Yvpn subscription/server link to clipboard.
3. Click `Import From Clipboard`.
4. Click central `CONNECT`.
5. Verify external IP changed and traffic goes through VPN.
6. Click `DISCONNECT` and verify traffic returns to direct path.
7. Restart app and verify state/settings persistence.
