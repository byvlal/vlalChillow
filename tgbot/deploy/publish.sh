#!/usr/bin/env bash
# Собрать бота в tgbot/publish (framework-dependent: на сервере есть .NET 8).
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

dotnet publish tgbot/StandChillow.Bot/StandChillow.Bot.csproj \
  -c Release \
  -o tgbot/publish \
  -p:DebugType=None \
  -p:DebugSymbols=false

echo
echo "Готово: $root/tgbot/publish"
echo "Дальше: скопируй каталог на сервер и запусти tgbot/deploy/install.sh"
