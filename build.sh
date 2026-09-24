#!/usr/bin/env bash
# Build with Mono's mcs (Linux/macOS, no NuGet needed).
#   RIMWORLD_MANAGED = folder containing Assembly-CSharp.dll, UnityEngine*.dll
#   HARMONY_DLL      = path to 0Harmony.dll (from the Harmony mod: Mods/Harmony/Current/Assemblies/0Harmony.dll)
set -euo pipefail
cd "$(dirname "$0")"
: "${RIMWORLD_MANAGED:?set RIMWORLD_MANAGED to the RimWorld .../Managed folder}"
: "${HARMONY_DLL:?set HARMONY_DLL to 0Harmony.dll}"
API="${MONO_API:-/usr/lib/mono/4.7.2-api}"
# Unity 2022 assemblies reference netstandard 2.1: prefer the copy RimWorld ships, else Mono's facade.
NETSTD="$RIMWORLD_MANAGED/netstandard.dll"; [ -f "$NETSTD" ] || NETSTD="$API/Facades/netstandard.dll"
mkdir -p 1.6/Assemblies
mcs -target:library -optimize+ -nostdlib -noconfig -langversion:7.2 -warn:4 \
  -out:1.6/Assemblies/Parametric.dll \
  -r:"$API/mscorlib.dll" -r:"$API/System.dll" -r:"$API/System.Core.dll" -r:"$NETSTD" \
  -r:"$RIMWORLD_MANAGED/Assembly-CSharp.dll" \
  -r:"$RIMWORLD_MANAGED/UnityEngine.CoreModule.dll" \
  -r:"$RIMWORLD_MANAGED/UnityEngine.IMGUIModule.dll" \
  -r:"$RIMWORLD_MANAGED/UnityEngine.TextRenderingModule.dll" \
  -r:"$HARMONY_DLL" \
  $(find Source/Parametric -name '*.cs' ! -path '*/obj/*' ! -path '*/bin/*')
rm -f 1.6/Assemblies/LoadSupport.dll  # obsolete pre-rename assembly must never load alongside
echo "Built 1.6/Assemblies/Parametric.dll"
