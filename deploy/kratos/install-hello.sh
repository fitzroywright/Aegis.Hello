#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="${1:-/tmp/aegis-hello-publish}"
INSTALL_DIR="/opt/aegis/hello"
STATE_DIR="/var/lib/aegis/hello"
ENV_DIR="/etc/aegis"
HELLO_ENV="$ENV_DIR/hello.env"
SERVICE_FILE="/etc/systemd/system/aegis-hello.service"
STAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_DIR="/opt/aegis/backups/hello-$STAMP"

if [[ ! -d "$SOURCE_DIR" ]]; then
  echo "Publish directory not found: $SOURCE_DIR" >&2
  exit 2
fi

if [[ ! -f "$SOURCE_DIR/Aegis.Hello.dll" ]]; then
  echo "Aegis.Hello.dll not found in $SOURCE_DIR" >&2
  exit 2
fi

install -d -m 0755 /opt/aegis/backups
install -d -o aegis -g aegis -m 0755 "$INSTALL_DIR"
install -d -o aegis -g aegis -m 0700 "$STATE_DIR"
install -d -m 0750 "$ENV_DIR"

if [[ -d "$INSTALL_DIR" ]] && find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 | grep -q .; then
  cp -a "$INSTALL_DIR" "$BACKUP_DIR"
  echo "Backed up existing Hello deployment to $BACKUP_DIR"
fi

systemctl stop aegis-hello.service 2>/dev/null || true

# Application binaries are replaceable. Durable registration identity lives in STATE_DIR
# and is intentionally outside INSTALL_DIR so rsync --delete cannot destroy it.
rsync -a --delete "$SOURCE_DIR/" "$INSTALL_DIR/"
chown -R aegis:aegis "$INSTALL_DIR"

if [[ -d "$INSTALL_DIR/scripts" ]]; then
  find "$INSTALL_DIR/scripts" -maxdepth 1 -type f \( -name '*.sh' -o -name 'aegis-hello' \) -exec chmod 0755 {} +
fi

if [[ ! -f "$HELLO_ENV" ]]; then
  cat > "$HELLO_ENV" <<'EOF'
# Aegis.Hello deployment overrides.
# Registration credentials do not belong here.
Aegis__Registration__IdentityFile=/var/lib/aegis/hello/registration-identity.json
EOF
fi

# Remove obsolete pre-shared registration material left by older deployments.
sed -i '/^AEGIS_REGISTRATION_KEY=/d' "$HELLO_ENV"
sed -i '/^AEGIS_CONFIGURATION_REGISTRATION_KEY=/d' "$HELLO_ENV"
sed -i '/^AegisConfiguration__RegistrationKey=/d' "$HELLO_ENV"
chown aegis:aegis "$HELLO_ENV"
chmod 0600 "$HELLO_ENV"

install -m 0644 "$SOURCE_DIR/deploy/kratos/aegis-hello.service" "$SERVICE_FILE"
install -m 0755 "$SOURCE_DIR/scripts/aegis-hello" /usr/local/bin/aegis-hello
systemctl daemon-reload
systemctl enable aegis-hello.service
systemctl restart aegis-hello.service

echo "Aegis Hello installed and started."
echo "Durable registration identity: $STATE_DIR/registration-identity.json"
echo "No registration key was generated or copied into an environment file."
