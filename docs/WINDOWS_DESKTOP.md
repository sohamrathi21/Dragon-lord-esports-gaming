# Windows desktop application

The Windows desktop workspace uses Microsoft Edge WebView2 to open the configured HTTPS café website in its own window. It provides no privileged device control or Windows lockdown. It uses the same server-side accounts and PostgreSQL data as the website; it does not create demo data. Windows 10/11 x64 and the Microsoft Edge WebView2 Runtime are required.

`services/DragonLord.Desktop/desktop-settings.json` configures the app name and HTTPS website URL. Build a self-contained .NET 8 release with:

```powershell
dotnet publish services/DragonLord.Desktop/DragonLord.Desktop.csproj -c Release -r win-x64 --self-contained true -o .runtime/desktop-publish
```

The MSI is a per-user installation with a Start menu shortcut and standard Windows uninstall support. It does not request administrator privileges. The initial release is unsigned; production distribution should use an Authenticode certificate. A desktop app is not a substitute for the Windows Service agent, venue controller, restricted Windows accounts or OS lockdown policies.

For an MSI, download the official WiX 3.14.1 binaries into `.runtime/wix` and run `scripts/build-windows.ps1`. The script publishes the app, builds a real MSI with Windows uninstall support, validates the package, and writes a SHA-256 checksum. Installation begins only when the user opens the MSI.
