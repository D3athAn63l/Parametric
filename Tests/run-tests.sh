#!/usr/bin/env bash
# Runs both test suites under Mono. Integration tests need RimWorld's Managed DLLs + 0Harmony.dll.
#   RIMWORLD_MANAGED=... HARMONY_DLL=... Tests/run-tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="${TEST_OUT:-$(mktemp -d)}"
API="${MONO_API:-/usr/lib/mono/4.7.2-api}"
NETSTD="${RIMWORLD_MANAGED:-/nonexistent}/netstandard.dll"; [ -f "$NETSTD" ] || NETSTD="$API/Facades/netstandard.dll"

echo "### Formula tests"
mcs -langversion:7.2 -out:"$OUT/FormulaTests.exe" Tests/FormulaTests.cs Source/Parametric/LoadSupport/LoadSupportFormula.cs
mono "$OUT/FormulaTests.exe"

echo; echo "### Integration tests"
: "${RIMWORLD_MANAGED:?set RIMWORLD_MANAGED}"; : "${HARMONY_DLL:?set HARMONY_DLL}"
cp "$RIMWORLD_MANAGED"/*.dll "$OUT/"; cp "$HARMONY_DLL" "$OUT/"; cp 1.6/Assemblies/Parametric.dll "$OUT/"
mcs -langversion:7.2 -nostdlib -noconfig -out:"$OUT/IntegrationTests.exe" \
  -r:"$API/mscorlib.dll" -r:"$API/System.dll" -r:"$API/System.Core.dll" -r:"$NETSTD" \
  -r:"$OUT/Assembly-CSharp.dll" -r:"$OUT/UnityEngine.CoreModule.dll" -r:"$OUT/0Harmony.dll" -r:"$OUT/Parametric.dll" \
  Tests/IntegrationTests.cs
(cd "$OUT" && mono IntegrationTests.exe)
