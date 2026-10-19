#!/bin/bash
# One-time: turns on Steam sign-in on the VPS (needs a race server of 0.6.0 or later installed).
#   sudo bash ~/wkrace-deploy/deploy/enable-steam.sh host.unlimitedgamesnogames.com
# Publishes https://<domain>/steam/, /privacy and /report (race server) and /download/ (WKSR downloads, put there by
# publish-wksr.sh) through nginx with the existing Let's Encrypt certificate, opens port 443, makes the certificate
# renewal also reload nginx, and starts the race server with --steam-public-url. Safe to run again. Restarts the race
# server (dropping matches in progress) only when its command line had to change.
set -euo pipefail

DOMAIN=${1:-}
UNIT=/etc/systemd/system/wkrace.service
LIVE=/etc/letsencrypt/live/$DOMAIN
HOOK=/etc/letsencrypt/renewal-hooks/deploy/wkrace.sh
SITE=/etc/nginx/sites-available/wkrace-steam

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo."; exit 1; }
[[ "$DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || { echo "Usage: sudo bash $0 <domain>"; exit 1; }
[ -f "$LIVE/fullchain.pem" ] || { echo "No certificate in $LIVE."; exit 1; }
command -v nginx >/dev/null || { echo "nginx is not installed (sudo apt-get install nginx)."; exit 1; }
[ -f "$UNIT" ] || { echo "$UNIT not found."; exit 1; }

echo "==== nginx: https://$DOMAIN/steam/"
cat > "$SITE" <<EOF
# WK Race: Steam sends players' browsers back here after they sign in (/steam/return).
# The race server answers on 127.0.0.1:47680. Nothing else is served on this address.
limit_req_zone \$binary_remote_addr zone=wkrace_steam:1m rate=2r/s;

server {
    listen 443 ssl;
    server_name $DOMAIN;
    server_tokens off;

    ssl_certificate $LIVE/fullchain.pem;
    ssl_certificate_key $LIVE/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;

    client_max_body_size 16k;
    # No access log: it would keep visitors' IP addresses (privacy). Errors are still logged.
    access_log off;

    location /steam/ {
        limit_req zone=wkrace_steam burst=10 nodelay;
        proxy_pass http://127.0.0.1:47680;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_read_timeout 30s;
    }

    # The privacy notice (race server 0.6.1 and later).
    location = /privacy {
        limit_req zone=wkrace_steam burst=10 nodelay;
        proxy_pass http://127.0.0.1:47680;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_read_timeout 30s;
    }

    # Bug reports from WKSR (race server 0.6.2 and later): up to 20 MB of screenshots. nginx takes the whole upload
    # before passing it on, so slow connections never hold the race server.
    location = /report {
        limit_req zone=wkrace_steam burst=10 nodelay;
        client_max_body_size 22m;
        client_body_timeout 60s;
        proxy_pass http://127.0.0.1:47680;
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_read_timeout 60s;
    }

    # WKSR downloads (race client): the zip, its page and latest.json, which WKSR reads to offer updates.
    location = /download {
        return 301 /download/;
    }
    location /download/ {
        limit_req zone=wkrace_steam burst=20 nodelay;
        alias /var/www/wksr/;
        index index.html;
        autoindex off;
        add_header X-Content-Type-Options nosniff;
    }

    location / {
        return 404;
    }
}
EOF
ln -sf "$SITE" /etc/nginx/sites-enabled/wkrace-steam
nginx -t
systemctl reload nginx

echo "==== Firewall"
ufw allow 443/tcp comment 'WK Race Steam sign-in'

echo "==== Certificate renewals also reload nginx"
if [ -f "$HOOK" ] && ! grep -q 'systemctl reload nginx' "$HOOK"; then
    echo 'systemctl reload nginx' >> "$HOOK"
fi

mkdir -p /var/www/wksr

echo "==== Race server"
changed=0
if ! grep -q -- '--steam-public-url' "$UNIT"; then
    changed=1
    if grep -q '^ExecStart=.*\\$' "$UNIT"; then
        echo "The ExecStart line in $UNIT continues over several lines; add ' --steam-public-url https://$DOMAIN' to it by hand."
        exit 1
    fi
    sed -i "s#^\(ExecStart=.*\)\$#\1 --steam-public-url https://$DOMAIN#" "$UNIT"
fi
grep '^ExecStart=' "$UNIT"
if [ "$changed" = 1 ]; then
    systemctl daemon-reload
    systemctl restart wkrace
    sleep 5
    journalctl -u wkrace -n 4 --no-pager -o cat
else
    echo "Already set up: the race server keeps running."
fi

echo "==== Check"
code=$(curl -s -o /dev/null -w '%{http_code}' "https://$DOMAIN/steam/return?n=check" || true)
echo "https://$DOMAIN/steam/return answers HTTP $code (expect 200: a page saying the link expired)"
code=$(curl -s -o /dev/null -w '%{http_code}' "https://$DOMAIN/privacy" || true)
echo "https://$DOMAIN/privacy answers HTTP $code (expect 200 from race server 0.6.1 on)"
code=$(curl -s -o /dev/null -w '%{http_code}' "https://$DOMAIN/download/latest.json" || true)
echo "https://$DOMAIN/download/latest.json answers HTTP $code (expect 200 once WKSR is published with publish-wksr.sh, 404 before)"
