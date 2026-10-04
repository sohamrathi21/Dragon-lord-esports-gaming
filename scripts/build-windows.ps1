param(
    [string]$DotNet = 'dotnet',
    [string]$WixDirectory = '.runtime/wix',
    [string]$Version = '0.1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$releaseDir = Join-Path $root '.runtime/windows-release'
$publishDir = Join-Path $root '.runtime/desktop-publish'
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
& $DotNet publish (Join-Path $root 'services/DragonLord.Desktop/DragonLord.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
$wixDir = (Resolve-Path $WixDirectory).Path
$files = Join-Path $releaseDir 'Files.wxs'
$script:nextId = 0
$script:componentRefs = [System.Text.StringBuilder]::new()
function Get-WixTree([string]$folder, [string]$directoryId) {
    $xml = [System.Text.StringBuilder]::new()
    foreach ($entry in Get-ChildItem -LiteralPath $folder | Sort-Object Name) {
        $script:nextId++
        $id = "Item$($script:nextId)"
        $name = [System.Security.SecurityElement]::Escape($entry.Name)
        if ($entry.PSIsContainer) {
            [void]$xml.Append("<Directory Id=`"Dir$id`" Name=`"$name`">")
            [void]$xml.Append((Get-WixTree $entry.FullName "Dir$id"))
            [void]$xml.Append('</Directory>')
        } elseif ($entry.Extension -ne '.pdb') {
            $source = [System.Security.SecurityElement]::Escape($entry.FullName)
            $guid = [Guid]::NewGuid().ToString()
            [void]$xml.Append("<Component Id=`"Component$id`" Guid=`"$guid`" Win64=`"yes`"><File Id=`"File$id`" Name=`"$name`" Source=`"$source`" /><RegistryValue Root=`"HKCU`" Key=`"Software\DragonLord\Desktop\Files`" Name=`"$id`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" /><RemoveFolder Id=`"Remove$id`" Directory=`"$directoryId`" On=`"uninstall`" /></Component>")
            [void]$script:componentRefs.Append("<ComponentRef Id=`"Component$id`" />")
        }
    }
    $cleanup = "Cleanup$directoryId"
    $cleanupGuid = [Guid]::NewGuid().ToString()
    [void]$xml.Append("<Component Id=`"$cleanup`" Guid=`"$cleanupGuid`" Win64=`"yes`"><RegistryValue Root=`"HKCU`" Key=`"Software\DragonLord\Desktop\Directories`" Name=`"$directoryId`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" /><RemoveFolder Id=`"Remove$cleanup`" Directory=`"$directoryId`" On=`"uninstall`" /></Component>")
    [void]$script:componentRefs.Append("<ComponentRef Id=`"$cleanup`" />")
    return $xml.ToString()
}
$tree = Get-WixTree $publishDir 'INSTALLFOLDER'
"<Wix xmlns=`"http://schemas.microsoft.com/wix/2006/wi`"><Fragment><DirectoryRef Id=`"INSTALLFOLDER`">$tree</DirectoryRef></Fragment><Fragment><ComponentGroup Id=`"DesktopFiles`">$($script:componentRefs.ToString())</ComponentGroup></Fragment></Wix>" | Set-Content -LiteralPath $files -Encoding utf8
$product = Join-Path $releaseDir 'Product.wxs'
@"
<?xml version="1.0" encoding="UTF-8"?>
<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">
  <Product Id="*" Name="Dragon Lord Esports Gaming" Language="1033" Version="$Version" Manufacturer="Dragon Lord Esports Gaming" UpgradeCode="2D6BC8EF-DCB2-4948-9A3D-6549BBE89875">
    <Package InstallerVersion="500" Compressed="yes" InstallScope="perUser" InstallPrivileges="limited" Platform="x64" />
    <MajorUpgrade DowngradeErrorMessage="A newer version is already installed." />
    <MediaTemplate EmbedCab="yes" CompressionLevel="mszip" />
    <Directory Id="TARGETDIR" Name="SourceDir">
      <Directory Id="LocalAppDataFolder"><Directory Id="INSTALLFOLDER" Name="Dragon Lord Esports Gaming" /></Directory>
      <Directory Id="ProgramMenuFolder"><Directory Id="ApplicationProgramsFolder" Name="Dragon Lord Esports Gaming" /></Directory>
    </Directory>
    <DirectoryRef Id="ApplicationProgramsFolder">
      <Component Id="AppShortcuts" Guid="4C8EAD78-02DA-44A5-83DB-81A9BA55DC56" Win64="yes">
        <Shortcut Id="WorkspaceShortcut" Name="Dragon Lord Esports Gaming" Target="[INSTALLFOLDER]DragonLord.Desktop.exe" WorkingDirectory="INSTALLFOLDER" Description="Open the café workspace" />
        <RemoveFolder Id="RemoveProgramsFolder" On="uninstall" />
        <RegistryValue Root="HKCU" Key="Software\DragonLord\Desktop" Name="installed" Type="integer" Value="1" KeyPath="yes" />
      </Component>
    </DirectoryRef>
    <Feature Id="Complete" Title="Desktop workspace" Level="1"><ComponentGroupRef Id="DesktopFiles" /><ComponentRef Id="AppShortcuts" /></Feature>
  </Product>
</Wix>
"@ | Set-Content -LiteralPath $product -Encoding utf8
& (Join-Path $wixDir 'candle.exe') -nologo -arch x64 "-dPublishDir=$publishDir" -out "$releaseDir\" $product $files
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $releaseDir "DragonLord-Setup-$Version-x64.msi"
& (Join-Path $wixDir 'light.exe') -nologo -ext WixUIExtension -out $installer (Join-Path $releaseDir 'Product.wixobj') (Join-Path $releaseDir 'Files.wixobj')
if ($LASTEXITCODE -ne 0) { throw 'Installer validation failed.' }
$checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$checksum  $(Split-Path $installer -Leaf)" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
Write-Output "Installer: $installer"
Write-Output "Size: $((Get-Item -LiteralPath $installer).Length) bytes"
