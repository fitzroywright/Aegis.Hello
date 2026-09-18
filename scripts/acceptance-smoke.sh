#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${AEGIS_HELLO_URL:-http://127.0.0.1:5400}"
IDENTITY_FILE="${AEGIS_HELLO_IDENTITY_FILE:-/var/lib/aegis/hello/registration-identity.json}"
ENV_FILE="${AEGIS_HELLO_ENV_FILE:-/etc/aegis/hello.env}"
SERVICE="${AEGIS_HELLO_SERVICE:-aegis-hello.service}"

pass=0
fail=0
blocked=0

result() {
  local state="$1" test_name="$2" evidence="$3"
  printf '%-8s | %-34s | %s\n' "$state" "$test_name" "$evidence"
  case "$state" in
    PASS) pass=$((pass+1)) ;;
    FAIL) fail=$((fail+1)) ;;
    BLOCKED) blocked=$((blocked+1)) ;;
  esac
}

json() {
  curl -fsS "$BASE_URL/status.json"
}

field() {
  python3 -c 'import json,sys; d=json.load(sys.stdin); print(eval(sys.argv[1]))' "$1"
}

printf '%-8s | %-34s | %s\n' "RESULT" "TEST" "EVIDENCE"
printf '%s\n' "-----------------------------------------------------------------------------------------------"

if curl -fsS "$BASE_URL/health" >/dev/null; then
  result PASS "Hello health endpoint" "$BASE_URL/health reachable"
else
  result FAIL "Hello health endpoint" "$BASE_URL/health unavailable"
fi

if payload="$(json 2>/dev/null)" && python3 -m json.tool >/dev/null <<<"$payload"; then
  result PASS "Machine-readable status" "/status.json is valid JSON"
else
  result FAIL "Machine-readable status" "/status.json unavailable or invalid"
  payload=""
fi

if command -v aegis-hello >/dev/null 2>&1 && aegis-hello status >/tmp/aegis-hello-status.txt 2>&1; then
  if grep -q "AEGIS HELLO STATUS" /tmp/aegis-hello-status.txt; then
    result PASS "Human-readable CLI" "aegis-hello status returned tabular status"
  else
    result FAIL "Human-readable CLI" "CLI did not return expected human-readable header"
  fi
else
  result FAIL "Human-readable CLI" "aegis-hello command unavailable or failed"
fi

if [[ -f "$ENV_FILE" ]]; then
  if grep -Eq '^(AEGIS_REGISTRATION_KEY|AEGIS_CONFIGURATION_REGISTRATION_KEY|AegisConfiguration__RegistrationKey)=' "$ENV_FILE"; then
    result FAIL "Legacy key environment audit" "legacy registration key found in $ENV_FILE"
  else
    result PASS "Legacy key environment audit" "no legacy registration key in $ENV_FILE"
  fi
else
  result BLOCKED "Legacy key environment audit" "$ENV_FILE not present"
fi

if [[ -f "$IDENTITY_FILE" ]]; then
  if [[ "$(stat -c '%a' "$IDENTITY_FILE")" == "600" ]]; then
    result PASS "Protected Linux identity store" "$IDENTITY_FILE mode is 0600"
  else
    result FAIL "Protected Linux identity store" "$IDENTITY_FILE mode is $(stat -c '%a' "$IDENTITY_FILE")"
  fi
else
  result BLOCKED "Protected Linux identity store" "identity file not created yet"
fi

if [[ -n "$payload" ]]; then
  before_installation="$(python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("installationId") or "")' <<<"$payload")"
  before_registration="$(python3 -c 'import json,sys; d=json.load(sys.stdin); r=d.get("registration") or {}; print(r.get("registrationId") or "")' <<<"$payload")"

  if [[ -n "$before_installation" ]]; then
    result PASS "InstallationId present" "$before_installation"
  else
    result FAIL "InstallationId present" "status did not report InstallationId"
  fi

  if systemctl restart "$SERVICE"; then
    for _ in {1..20}; do
      sleep 1
      after_payload="$(json 2>/dev/null || true)"
      [[ -n "$after_payload" ]] && break
    done

    after_installation="$(python3 -c 'import json,sys; d=json.load(sys.stdin); print(d.get("installationId") or "")' <<<"${after_payload:-{}}")"
    after_registration="$(python3 -c 'import json,sys; d=json.load(sys.stdin); r=d.get("registration") or {}; print(r.get("registrationId") or "")' <<<"${after_payload:-{}}")"

    if [[ -n "$before_installation" && "$before_installation" == "$after_installation" ]]; then
      result PASS "Restart preserves InstallationId" "$before_installation"
    else
      result FAIL "Restart preserves InstallationId" "before=$before_installation after=$after_installation"
    fi

    if [[ -n "$before_registration" && "$before_registration" == "$after_registration" ]]; then
      result PASS "Restart preserves registration" "$before_registration"
    elif [[ -z "$before_registration" ]]; then
      result BLOCKED "Restart preserves registration" "registration not established before restart"
    else
      result FAIL "Restart preserves registration" "before=$before_registration after=$after_registration"
    fi
  else
    result FAIL "Service restart" "systemctl restart $SERVICE failed"
  fi
fi

if [[ -f "$IDENTITY_FILE" ]] && grep -aEq 'AEGIS_REGISTRATION_KEY|ConfigurationRegistrationKey' "$IDENTITY_FILE"; then
  result FAIL "Identity file legacy-string scan" "legacy registration term found"
elif [[ -f "$IDENTITY_FILE" ]]; then
  result PASS "Identity file legacy-string scan" "no legacy registration key names"
else
  result BLOCKED "Identity file legacy-string scan" "identity file not present"
fi

printf '%s\n' "-----------------------------------------------------------------------------------------------"
printf 'PASS=%d FAIL=%d BLOCKED=%d\n' "$pass" "$fail" "$blocked"

if (( fail > 0 )); then
  exit 1
fi

if (( blocked > 0 )); then
  exit 2
fi

exit 0
