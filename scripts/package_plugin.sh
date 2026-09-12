#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "$0")/.." && pwd)
cd "$repo"
version=0.1.2
package="$repo/dist/jellyfin-plugin-jable-$version.zip"
[[ ! -L dist && ! -L dist/plugin ]] || { echo 'Refusing symlinked dist paths' >&2; exit 1; }

docker run --rm -v "$repo:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet test Jellyfin.Plugin.Jable.sln -c Release
node --test web-tests/jable.test.mjs
python3 scripts/merge_web_config.py --self-test

mkdir -p dist
rm -rf -- "$repo/dist/plugin"
rm -f -- "$package"
docker run --rm -v "$repo:/src" -w /src mcr.microsoft.com/dotnet/sdk:8.0 \
    dotnet publish Jellyfin.Plugin.Jable/Jellyfin.Plugin.Jable.csproj -c Release -o /src/dist/plugin
cp Jellyfin.Plugin.Jable/build.yaml dist/plugin/build.yaml
python3 -m zipfile -c "$package" \
    dist/plugin/Jellyfin.Plugin.Jable.dll dist/plugin/build.yaml
shasum -a 256 "$package"
