#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-dotnet}"
if [[ -n "${CSS_ROOT:-}" ]]; then
  mkdir -p refs
  cp "$CSS_ROOT/api/CounterStrikeSharp.API.dll" refs/
  cp "$CSS_ROOT/api/Microsoft.Extensions.Logging.Abstractions.dll" refs/
  cp "$CSS_ROOT/shared/BotControllerApi/BotControllerApi.dll" refs/
fi
test -f refs/BotControllerApi.dll
test -f third_party/CS2-Bot-Controller/csharp/BotControllerImpl/BotController.NativeApi.cs
"$DOTNET" build src/KeranStrat.BotControllerBridge -c Release
"$DOTNET" build src/KeranStrat.Plugin -c Release
"$DOTNET" run --project tests -c Release
