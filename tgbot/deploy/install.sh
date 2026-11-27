#!/usr/bin/env bash
# Установить уже собранного бота на этот сервер.
# Запуск: sudo tgbot/deploy/install.sh [каталог-с-публикацией]
#
# По умолчанию берёт tgbot/publish рядом с репозиторием. .env не перезаписывает:
# если файла нет — кладёт .env.example, чтобы владелец заполнил его сам.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="${1:-$here/../publish}"
dest="${BOT_DIR:-/opt/vlalchillow-tgbot}"
bot_user="${BOT_USER:-vlal}"

if [[ ! -f "$src/StandChillow.Bot.dll" ]]; then
  echo "Нет $src/StandChillow.Bot.dll — сначала собери: tgbot/deploy/publish.sh" >&2
  exit 1
fi

if ! id "$bot_user" >/dev/null 2>&1; then
  echo "Пользователя $bot_user нет. Задай BOT_USER=... или создай его." >&2
  exit 1
fi

install -d -m 755 -o "$bot_user" -g "$bot_user" "$dest"
cp -a "$src/." "$dest/"
install -d -m 700 -o "$bot_user" -g "$bot_user" "$dest/data"

if [[ ! -f "$dest/.env" ]]; then
  if [[ -f "$dest/.env.example" ]]; then
    install -m 600 -o "$bot_user" -g "$bot_user" "$dest/.env.example" "$dest/.env"
    echo "Создан $dest/.env — заполни TELEGRAM_BOT_TOKEN и ключ бота."
  else
    echo "В публикации нет .env.example — создай $dest/.env вручную." >&2
  fi
else
  chmod 600 "$dest/.env"
  chown "$bot_user:$bot_user" "$dest/.env"
fi

unit_tmp="$(mktemp)"
sed "s/^User=.*/User=$bot_user/; s/^Group=.*/Group=$bot_user/" \
  "$here/vlalchillow-tgbot.service" > "$unit_tmp"
install -m 644 "$unit_tmp" /etc/systemd/system/vlalchillow-tgbot.service
rm -f "$unit_tmp"

systemctl daemon-reload
systemctl enable vlalchillow-tgbot

# Пустой токен — не повод крутить падающий сервис по кругу: говорим, что заполнить,
# и оставляем юнит включённым. Владелец впишет токен и запустит сам.
if grep -qE '^TELEGRAM_BOT_TOKEN=[[:space:]]*$' "$dest/.env"; then
  systemctl stop vlalchillow-tgbot 2>/dev/null || true
  echo
  echo "TELEGRAM_BOT_TOKEN пуст — сервис не запущен."
  echo "Впиши токен от @BotFather в $dest/.env и запусти:"
  echo "  sudo systemctl start vlalchillow-tgbot"
  exit 0
fi

systemctl restart vlalchillow-tgbot
sleep 1
systemctl --no-pager --full status vlalchillow-tgbot || true

echo
echo "Логи: journalctl -u vlalchillow-tgbot -f"
