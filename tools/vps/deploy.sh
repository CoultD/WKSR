#!/bin/bash
# Installs this WK Race server build on the VPS. Run from the extracted linux-x64 package:
#   sudo bash ~/wkrace-deploy/deploy/deploy.sh
# Stops the server (matches in progress are dropped), backs up the data, swaps the program folder (the old one is
# kept as /opt/wkrace.prev) and starts the new one. Every step is safe to repeat. Also makes sure of:
#   - the database key /etc/wkrace/db.key and the admin key /etc/wkrace/admin.key (each created once) and the systemd
#     unit passing them to the server;
#   - the finish-time checks (--min-finish-seconds / --min-finish-splits) in the unit;
#   - the admin command (sudo wkrace-admin ...), the daily backup timer, the 5-minute health check (wkrace-monitor)
#     and the mailer for bug reports and alerts (wkrace-mail; set up once with sudo wkrace-mail setup);
#   - log retention of 21 days (system journal) for the privacy notice.
set -euo pipefail

PKG=$(cd "$(dirname "$0")/.." && pwd)
INSTALL=/opt/wkrace
UNIT=/etc/systemd/system/wkrace.service
KEY=/etc/wkrace/db.key
ADMIN_KEY=/etc/wkrace/admin.key

# Appends " <flag> <value>" to the unit's ExecStart line unless the flag is already there.
add_flag() {
    grep -q -- "$1" "$UNIT" && return 0
    if grep -q '^ExecStart=.*\\$' "$UNIT"; then
        echo "The ExecStart line in $UNIT continues over several lines; add ' $1 $2' to it by hand."
        exit 1
    fi
    sed -i "s#^\(ExecStart=.*\)\$#\1 $1 $2#" "$UNIT"
}

[ "$(id -u)" -eq 0 ] || { echo "Run with sudo: sudo bash $0"; exit 1; }
[ -f "$PKG/WKRace.Server" ] || { echo "WKRace.Server not found in $PKG. Extract the whole package first."; exit 1; }
[ -f "$UNIT" ] || { echo "$UNIT not found: this script updates an existing install."; exit 1; }

if [ "${1:-}" != "--yes" ]; then
    read -r -p "This restarts the race server; matches in progress are dropped. Continue? [y/N] " answer
    [[ "$answer" =~ ^[Yy] ]] || { echo "Nothing changed."; exit 0; }
fi

echo "==== Database key"
new_key=0
if [ ! -f "$KEY" ]; then
    (umask 077; head -c 48 /dev/urandom | base64 -w0 > "$KEY")
    new_key=1
    echo "Created $KEY"
fi
chown root:root "$KEY"
chmod 600 "$KEY"
# systemd hands the key to the service as a private credential file; it never appears in the command line or the environment.
grep -q '^LoadCredential=dbkey:' "$UNIT" || sed -i "/^\[Service\]/a LoadCredential=dbkey:$KEY" "$UNIT"
add_flag --db-key-credential dbkey

echo "==== Admin key"
if [ ! -f "$ADMIN_KEY" ]; then
    (umask 077; head -c 32 /dev/urandom | base64 -w0 > "$ADMIN_KEY")
    echo "Created $ADMIN_KEY (no need to save it elsewhere: a new one can be made at any time)"
fi
chown root:root "$ADMIN_KEY"
chmod 600 "$ADMIN_KEY"
grep -q '^LoadCredential=adminkey:' "$UNIT" || sed -i "/^\[Service\]/a LoadCredential=adminkey:$ADMIN_KEY" "$UNIT"
add_flag --admin-key-credential adminkey
cat > /usr/local/bin/wkrace-admin <<'WRAPPER'
#!/bin/sh
# Race server admin commands: sudo wkrace-admin help
exec /opt/wkrace/WKRace.Server admin --admin-key-file /etc/wkrace/admin.key "$@"
WRAPPER
chmod 755 /usr/local/bin/wkrace-admin

echo "==== Finish-time checks"
# A finish faster than 3 minutes, or with fewer than 15 level splits, is disqualified.
add_flag --min-finish-seconds 180
add_flag --min-finish-splits 15
grep '^ExecStart=' "$UNIT"

echo "==== Proximity voice (UDP 47601)"
# The race server relays voice chat on UDP 47601 (its default); the firewall has to let it in.
if command -v ufw >/dev/null; then
    ufw allow 47601/udp comment 'WK Race voice' >/dev/null && echo "ufw: 47601/udp open"
fi

echo "==== Backup tools"
install -m 755 "$PKG/deploy/wkrace-backup" /usr/local/bin/wkrace-backup
install -m 644 "$PKG/deploy/wkrace-backup.service" /etc/systemd/system/wkrace-backup.service
install -m 644 "$PKG/deploy/wkrace-backup.timer" /etc/systemd/system/wkrace-backup.timer

echo "==== Health check"
install -m 755 "$PKG/deploy/wkrace-monitor" /usr/local/bin/wkrace-monitor
install -m 644 "$PKG/deploy/wkrace-monitor.service" /etc/systemd/system/wkrace-monitor.service
install -m 644 "$PKG/deploy/wkrace-monitor.timer" /etc/systemd/system/wkrace-monitor.timer

echo "==== Email (bug reports and alerts)"
install -m 755 "$PKG/deploy/wkrace-mail" /usr/local/bin/wkrace-mail
install -m 644 "$PKG/deploy/wkrace-mail.service" /etc/systemd/system/wkrace-mail.service
install -m 644 "$PKG/deploy/wkrace-mail.timer" /etc/systemd/system/wkrace-mail.timer

echo "==== Log retention (21 days)"
mkdir -p /etc/systemd/journald.conf.d
printf '[Journal]\nMaxRetentionSec=21day\n' > /etc/systemd/journald.conf.d/60-retention.conf
systemctl restart systemd-journald

systemctl daemon-reload
systemctl enable --now wkrace-backup.timer >/dev/null
systemctl enable --now wkrace-monitor.timer >/dev/null
systemctl enable --now wkrace-mail.timer >/dev/null

echo "==== Install"
rm -rf "$INSTALL.new"
mkdir -p "$INSTALL.new"
cp -a "$PKG/." "$INSTALL.new/"
rm -rf "$INSTALL.new/deploy"
chown -R root:root "$INSTALL.new"
chmod -R go-w "$INSTALL.new"
chmod 755 "$INSTALL.new/WKRace.Server"

# Stopped first, so the backup is of cleanly saved data rather than a live database.
systemctl stop wkrace
# A new version can change the database (0.9.1 adds the leaderboard tables), so nothing is installed without a backup.
if ! /usr/local/bin/wkrace-backup pre-update; then
    echo "The backup failed, so nothing was installed. Starting the server that was running again."
    systemctl start wkrace
    exit 1
fi
rm -rf "$INSTALL.prev"
[ -d "$INSTALL" ] && mv "$INSTALL" "$INSTALL.prev"
mv "$INSTALL.new" "$INSTALL"
systemctl start wkrace
sleep 5

if systemctl is-active --quiet wkrace; then
    echo "Race server: running"
    journalctl -u wkrace -n 5 --no-pager -o cat
else
    echo "Race server did NOT start. Last log lines:"
    journalctl -u wkrace -n 20 --no-pager -o cat
    echo
    echo "To roll back: sudo systemctl stop wkrace && sudo rm -rf $INSTALL && sudo mv $INSTALL.prev $INSTALL && sudo systemctl start wkrace"
    echo "(Going back to a version before the database (0.4.x) also needs the imported JSON renamed back:"
    echo " /var/lib/wkrace/players.json.imported-* -> players.json, and matches.jsonl.imported-* -> matches.jsonl.)"
    exit 1
fi

echo "==== Checks"
echo "Daily data backup: $(systemctl is-active wkrace-backup.timer) (backups in /var/backups/wkrace)"
echo "Health check every 5 minutes: $(systemctl is-active wkrace-monitor.timer)"
if [ -s /etc/wkrace/mail.conf ]; then
    echo "Email: set up (bug reports every 2 minutes, alerts). Test: sudo wkrace-mail test"
else
    echo "Email: NOT set up, so bug reports wait on the server and alerts are not emailed. Run: sudo wkrace-mail setup"
fi
if admin_out=$(/usr/local/bin/wkrace-admin status 2>&1); then
    echo "Admin command: OK (sudo wkrace-admin help)"
else
    echo "Admin command: $admin_out"
fi
echo "Automatic security updates: $(systemctl is-enabled unattended-upgrades 2>/dev/null || echo 'not installed')"
if ls /var/lib/wkrace/*.imported-* >/dev/null 2>&1; then
    echo
    echo "Imported from the old JSON files (plain text, not encrypted):"
    ls -1 /var/lib/wkrace/*.imported-*
    echo "Once you have checked that players and ratings are all there, delete them:  sudo rm /var/lib/wkrace/*.imported-*"
fi
if [ "$new_key" = 1 ]; then
    echo
    echo "!!!! A new database key was created: $KEY"
    echo "!!!! Without it the database and every backup of it are unreadable. Save a copy somewhere safe"
    echo "!!!! (a password manager), NOT on this server:   sudo cat $KEY"
fi
