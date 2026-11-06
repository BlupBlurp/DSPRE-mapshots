#!/usr/bin/env bash
# Builds DSPRE and renders map screenshots without opening the GUI.
# Extra arguments are passed to the batch: ./mapshots.sh --maps 47,133 --out shots
set -euo pipefail
cd "$(dirname "$0")"

dotnet build DSPRE.Avalonia/DSPRE.Avalonia.csproj -c Release -v q --nologo
dotnet DSPRE.Avalonia/bin/Release/net8.0/DSPRE.Avalonia.dll --map-shots --config mapshots.json "$@"
