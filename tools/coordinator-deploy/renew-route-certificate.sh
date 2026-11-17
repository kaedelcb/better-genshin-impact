#!/usr/bin/env bash
# 由部署用户的 crontab 调用，仅续期本任务证书；不读取、导出口令或私钥。
set -euo pipefail
exec 9</opt/bgi-coordinator
flock -n -x 9 || exit 0

docker exec nginx-app-1 /opt/certbot/bin/certbot renew \
    --config /etc/letsencrypt.ini \
    --work-dir /tmp/letsencrypt-lib \
    --logs-dir /tmp/letsencrypt-log \
    --cert-name bgi-coordinator-www-autobgi-cn \
    --non-interactive --no-random-sleep-on-renew --quiet \
    --deploy-hook 'nginx -t && nginx -s reload'
