# Publishes Antril's language pack natively into artifacts/languages/Antril: the frontend as a native library (its entry
# points exported), its manifest and its file icon. Larva's installer build (tools/publish-larva.ps1 in the Larva
# repository) takes the packs from artifacts/languages, completes their manifests and signs them.
#
#   tools/publish-pack.ps1            the runtime packs from NuGet
#   tools/publish-pack.ps1 -Offline   only what the local NuGet cache has
param([switch]$Offline)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$language = "$root/src/Antril"
$build = "$root/artifacts/obj/native"
$pack = "$root/artifacts/languages/Antril"
# The native linker is found through vswhere, which Visual Studio installs outside PATH.
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;" + $env:PATH
$arguments = @("publish", "$language/Antril.csproj", "-c", "Release", "-r", "win-x64", "-p:PublishAot=true", "-p:NativeLib=Shared", "-o", $build)
if ($Offline) {
    $arguments += @("-p:RuntimeFrameworkVersion=10.0.12", "--source", "$env:USERPROFILE/.nuget/packages")
}
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$manifest = Get-Content -Raw "$language/language.json" | ConvertFrom-Json
if (Test-Path $pack) { Remove-Item -Recurse -Force $pack }
New-Item -ItemType Directory -Force $pack | Out-Null
Copy-Item "$build/$($manifest.entry)" "$pack/$($manifest.entry)"
Copy-Item "$language/language.json" "$pack/language.json"
# The icon of Antril's files (the Larva installer gives .ant files this icon).
if ($manifest.icon) { Copy-Item "$language/$($manifest.icon)" "$pack/$($manifest.icon)" }
Write-Host "pack: $pack"
exit 0
