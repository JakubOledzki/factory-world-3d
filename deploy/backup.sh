#!/bin/sh
# A copy of the game's database to deploy/backups (pg_dump custom format), keeping the last 14 days.
# Run in deploy/ by hand or daily from cron (deploy/README.md).
set -eu
cd "$(dirname "$0")"
mkdir -p backups
file="backups/factoryworld3d-$(date +%Y-%m-%d-%H%M).dump"
docker compose exec -T db pg_dump -U factory -Fc factoryworld3d > "$file"
echo "$file: $(du -h "$file" | cut -f1)"
find backups -name 'factoryworld3d-*.dump' -mtime +14 -delete
