#!/usr/bin/env bash
# Rebuilds every IntelliSearch nanoservice as a net9.0 Release class library and copies
# the DLL + PDB (+ XML docs when present) into ./lib next to this script.
#
# The nanoservice projects declare net10.0; only the .NET 9 SDK is available and the
# test project targets net9.0, so the framework is overridden on the command line.
# No nanoservice source or project file is modified.
# Restore and build are separate steps because NuGet restore ignores a global
# TargetFramework property.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NANO="$HERE/../../nanoservices"
LIB="$HERE/lib"
TFM="net9.0"
mkdir -p "$LIB"

PROJECTS=(
  "ValidateRequestService/src/ValidateRequest/ValidateRequest.csproj"
  "TraversalDirectoryService/src/TraverseDirectory/TraverseDirectory.csproj"
  "SplitIntoChunksService/src/SplitPathsIntoChunks/SplitPathsIntoChunks.csproj"
  "PatternMatchingService/src/PatternMatching/PatternMatching.csproj"
  "SearchContentService/SearchContent/SearchContent.csproj"
)

for rel in "${PROJECTS[@]}"; do
  proj="$NANO/$rel"
  name="$(basename "$rel" .csproj)"
  echo "=== $name"
  dotnet restore "$proj" -p:TargetFramework="$TFM" -nologo
  dotnet build "$proj" --no-restore -c Release -p:TargetFramework="$TFM" -nologo
  out="$(dirname "$proj")/bin/Release/$TFM"
  cp "$out/$name.dll" "$out/$name.pdb" "$LIB/"
  if [[ -f "$out/$name.xml" ]]; then cp "$out/$name.xml" "$LIB/"; fi
done

echo "Libraries in $LIB:"
ls -l "$LIB"
