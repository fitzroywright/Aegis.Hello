#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="${1:-/tmp/aegis-hello-publish}"
INSTALL_DIR="/opt/aegis/hello"
ENV_DIR="/etc/aegis"
ENV_FILE="$ENV_DIR/hello.env"
SERVICE_FILE="/etc/systemd/system/aegis-hello.service"
OPERATIONS_DROPIN_DIR="/etc/systemd/system/aegis-operations.service.d"
OPERATIONS_DROPIN="$OPERATIONS_DROPIN_DIR/hello.conf"
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
install -d -m 0750 "$ENV_DIR"

if [[ -d "$INSTALL_DIR" ]] && find "$INSTALL_DIR" -mindepth 1 -maxdepth 1 | grep -q .; then
  cp -a "$INSTALL_DIR" "$BACKUP_DIR"
  echo "Backed up existing Hello deployment to $BACKUP_DIR"
fi

systemctl stop aegis-hello.service 2>/dev/null || true

rsync -a --delete "$SOURCE_DIR/" "$INSTALL_DIR/"
chown -R aegis:aegis "$INSTALL_DIR"

if [[ ! -f "$ENV_FILE" ]]; then
  cat > "$ENV_FILE" <<'EOF'
# Aegis.Hello secrets and deployment overrides.
# Keep this file out of source control.
# Required before Hello can register with Configuration:
# AEGIS_CONFIGURATION_REGISTRATION_KEY=<same value used by Aegis.Configuration>
EOF
  chown aegis:aegis "$ENV_FILE"
  chmod 0600 "$ENV_FILE"
  echo "Created $ENV_FILE. Registration key still needs to be added."
else
  chown aegis:aegis "$ENV_FILE"
  chmod 0600 "$ENV_FILE"
  echo "Preserved existing $ENV_FILE"
fi

install -m 0644 "$SOURCE_DIR/deploy/kratos/aegis-hello.service" "$SERVICE_FILE"

install -d -m 0755 "$OPERATIONS_DROPIN_DIR"
cat > "$OPERATIONS_DROPIN" <<'EOF'
[Unit]
Wants=aegis-hello.service
After=aegis-hello.service
EOF
chmod 0644 "$OPERATIONS_DROPIN"

systemctl daemon-reload
systemctl enable aegis-hello.service

if grep -Eq '^[[:space:]]*(AEGIS_CONFIGURATION_REGISTRATION_KEY|Aegis__Configuration__RegistrationKey)=' "$ENV_FILE"; then
  systemctl restart aegis-hello.service
  echo "Aegis Hello started."
else
  echo "Aegis Hello installed but not started because no registration key is configured in $ENV_FILE."
  echo "After adding the key, run: systemctl restart aegis-hello"
fi

echo "Operations drop-in installed at $OPERATIONS_DROPIN"
echo "Starting aegis-operations will now also start aegis-hello."
