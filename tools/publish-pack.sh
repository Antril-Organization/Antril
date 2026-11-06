#!/bin/sh
# Publishes Antril's language pack natively into artifacts/languages/Antril on Linux or macOS (what publish-pack.ps1 does
# on Windows): the frontend as a native shared library, its manifest naming it (X.so or X.dylib, where language.json
# says X.dll) and its file icon as .svg (Linux) and .icns (macOS). Larva's installer build signs it.
set -eu
root=$(cd "$(dirname "$0")/.." && pwd)
language="$root/src/Antril"
build="$root/artifacts/obj/native"
pack="$root/artifacts/languages/Antril"

case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64; library=so ;;
    Darwin-arm64) rid=osx-arm64; library=dylib ;;
    *) echo "publish-pack.sh: packs are published for Linux x64 and macOS arm64, not $(uname -s) $(uname -m)" >&2; exit 2 ;;
esac

dotnet publish "$language/Antril.csproj" -c Release -r "$rid" -p:PublishAot=true -p:NativeLib=Shared -o "$build"
entry=$(sed -n 's/.*"entry"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$language/language.json" | head -n 1)
native="${entry%.*}.$library"
rm -rf "$pack"
mkdir -p "$pack"
cp "$build/$native" "$pack/$native"
sed "s/\"entry\"\([[:space:]]*:[[:space:]]*\)\"[^\"]*\"/\"entry\"\1\"$native\"/" "$language/language.json" > "$pack/language.json"
icon=$(sed -n 's/.*"icon"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$language/language.json" | head -n 1)
if [ -n "$icon" ]; then
    for kind in svg icns; do
        if [ -f "$language/${icon%.*}.$kind" ]; then cp "$language/${icon%.*}.$kind" "$pack/"; fi
    done
fi
echo "pack: $pack"
