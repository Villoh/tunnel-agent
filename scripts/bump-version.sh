#!/usr/bin/env bash
set -euo pipefail

VERSION="${1#v}"
SEMVER="${VERSION%%-*}"

find . -name "*.csproj" ! -name "TunnelAgent.Tests.csproj" \
  ! -path "*/obj/*" ! -path "*/bin/*" | while read -r f; do
  sed -i.bak \
    -e "s|<Version>[^<]*</Version>|<Version>${VERSION}</Version>|g" \
    -e "s|<AssemblyVersion>[^<]*</AssemblyVersion>|<AssemblyVersion>${SEMVER}.0</AssemblyVersion>|g" \
    -e "s|<FileVersion>[^<]*</FileVersion>|<FileVersion>${SEMVER}.0</FileVersion>|g" \
    "$f" && rm -f "$f.bak"
  echo "  Bumped $(basename "$f") -> $VERSION"
done

echo "Version bumped to $VERSION"
