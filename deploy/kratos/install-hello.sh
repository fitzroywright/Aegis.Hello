#!/usr/bin/env bash
set -euo pipefail

SOURCE_DIR="${1:-/tmp/aegis-hello-publish}"
INSTALL_DIR="/opt/aegis/hello"
ENV_DIR="/etc/aegis"
HELLO_ENV="$ENV_DIR/hello.env"
CONFIG_ENV="$ENV_DIR/configuration.env"
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

if [[ ! -f "$CONFIG_ENV" ]]; then
  echo "Configuration environment file not found: $CONFIG_ENV" >&2
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

CONFIG_KEY_CREATED=0
REGKEY="$(sed -n 's/^[[:space:]]*AegisConfiguration__RegistrationKey=//p' "$CONFIG_ENV" | tail -n 1)"

if [[ -z "$REGKEY" ]]; then
  REGKEY="$(openssl rand -hex 32)"
  printf '\nAegisConfiguration__RegistrationKey=%s\n' "$REGKEY" >> "$CONFIG_ENV"
  chown aegis:aegis "$CONFIG_ENV"
  chmod 0600 "$CONFIG_ENV"
  CONFIG_KEY_CREATED=1
  echo "Created Aegis.Configuration registration credential in $CONFIG_ENV."
else
  echo "Reusing existing Aegis.Configuration registration credential."
fi

if [[ ! -f "$HELLO_ENV" ]]; then
  cat > "$HELLO_ENV" <<'EOF'
# Aegis.Hello secrets and deployment overrides.
# Keep this file out of source control.
EOF
fi

if grep -q '^AEGIS_CONFIGURATION_REGISTRATION_KEY=' "$HELLO_ENV"; then
  sed -i "s/^AEGIS_CONFIGURATION_REGISTRATION_KEY=.*/AEGIS_CONFIGURATION_REGISTRATION_KEY=$REGKEY/" "$HELLO_ENV"
else
  printf 'AEGIS_CONFIGURATION_REGISTRATION_KEY=%s\n' "$REGKEY" >> "$HELLO_ENV"
fi
chown aegis:aegis "$HELLO_ENV"
chmod 0600 "$HELLO_ENV"
unset REGKEY

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

if [[ "$CONFIG_KEY_CREATED" -eq 1 ]]; then
  systemctl restart aegis-configuration.service
fi

systemctl restart aegis-hello.service

echo "Aegis Hello installed and started."
echo "Operations drop-in installed at $OPERATIONS_DROPIN"
echo "Starting aegis-operations will now also start aegis-hello."
