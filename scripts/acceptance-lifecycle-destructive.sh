#!/usr/bin/env bash
set -euo pipefail

: "${AEGIS_ACCEPTANCE_DESTRUCTIVE:?Set AEGIS_ACCEPTANCE_DESTRUCTIVE=YES to acknowledge destructive lifecycle testing.}"
[[ "$AEGIS_ACCEPTANCE_DESTRUCTIVE" == "YES" ]] || {
  echo "Refusing destructive lifecycle test: AEGIS_ACCEPTANCE_DESTRUCTIVE must equal YES." >&2
  exit 2
}

HELLO_URL="${AEGIS_HELLO_URL:-http://127.0.0.1:5400}"
OPERATIONS_URL="${AEGIS_OPERATIONS_URL:-https://operations.ffpja.org}"
COOKIE_JAR="${AEGIS_OPERATIONS_COOKIE_JAR:-}"
IDENTITY_FILE="${AEGIS_HELLO_IDENTITY_FILE:-/var/lib/aegis/hello/registration-identity.json}"
SERVICE="${AEGIS_HELLO_SERVICE:-aegis-hello.service}"

[[ -n "$COOKIE_JAR" && -f "$COOKIE_JAR" ]] || {
  echo "Set AEGIS_OPERATIONS_COOKIE_JAR to an authenticated curl cookie jar for an account with Configuration.Manage." >&2
  exit 2
}
[[ -f "$IDENTITY_FILE" ]] || {
  echo "Hello identity file not found: $IDENTITY_FILE" >&2
  exit 2
}

pass=0
fail=0

report() {
  local state="$1" name="$2" evidence="$3"
  printf '%-5s | %-42s | %s\n' "$state" "$name" "$evidence"
  if [[ "$state" == "PASS" ]]; then pass=$((pass+1)); else fail=$((fail+1)); fi
}

hello_json() {
  curl -fsS "$HELLO_URL/status.json"
}

hello_value() {
  local expression="$1"
  hello_json | python3 -c "import json,sys; d=json.load(sys.stdin); print($expression)"
}

wait_state() {
  local wanted="$1" timeout="${2:-120}" started now state
  started="$(date +%s)"
  while true; do
    state="$(hello_value '(d.get("registration") or {}).get("state") or ""' 2>/dev/null || true)"
    if [[ "$state" == "$wanted" ]]; then
      return 0
    fi
    now="$(date +%s)"
    if (( now - started >= timeout )); then
      echo "Timed out waiting for Hello state $wanted; current state=${state:-unknown}" >&2
      return 1
    fi
    sleep 2
  done
}

registration_id() {
  hello_value '(d.get("registration") or {}).get("registrationId") or ""'
}

installation_id() {
  hello_value 'd.get("installationId") or ""'
}

ops_action() {
  local id="$1" action="$2" reason="$3"
  curl -fsS -b "$COOKIE_JAR" \
    -H 'content-type: application/json' \
    -X POST \
    "$OPERATIONS_URL/api/lifecycle-registrations/${id}/${action}" \
    --data "$(python3 -c 'import json,sys; print(json.dumps({"reason":sys.argv[1]}))' "$reason")"
}

ops_purge() {
  local id="$1" reason="$2"
  curl -fsS -b "$COOKIE_JAR" \
    -H 'content-type: application/json' \
    -X DELETE \
    "$OPERATIONS_URL/api/lifecycle-registrations/${id}" \
    --data "$(python3 -c 'import json,sys; print(json.dumps({"reason":sys.argv[1]}))' "$reason")"
}

approve_current() {
  local id
  id="$(registration_id)"
  [[ -n "$id" ]] || return 1
  ops_action "$id" approve "Automated Aegis.Hello acceptance approval" >/dev/null
  wait_state Registered 120
}

printf '%-5s | %-42s | %s\n' "STATE" "TEST" "EVIDENCE"
printf '%s\n' "------------------------------------------------------------------------------------------------"

state="$(hello_value '(d.get("registration") or {}).get("state") or ""' 2>/dev/null || true)"
if [[ "$state" == "Pending" || "$state" == "RecoveryPending" ]]; then
  if approve_current; then
    report PASS "Initial registration approval" "Hello reached Registered"
  else
    report FAIL "Initial registration approval" "Hello failed to reach Registered"
    exit 1
  fi
elif [[ "$state" == "Registered" ]]; then
  report PASS "Initial registered state" "Hello already Registered"
else
  report FAIL "Initial registered state" "unexpected state=${state:-unknown}"
  exit 1
fi

original_installation="$(installation_id)"
original_registration="$(registration_id)"

# 1. Credential-loss recovery: preserve RegistrationId + InstallationId, remove only credential/claim.
systemctl stop "$SERVICE"
python3 - "$IDENTITY_FILE" <<'PY'
import json,sys
p=sys.argv[1]
with open(p,encoding="utf-8") as f:
    d=json.load(f)
d["credential"]=None
d["claimToken"]=None
with open(p,"w",encoding="utf-8") as f:
    json.dump(d,f,indent=2)
    f.write("\n")
PY
chmod 600 "$IDENTITY_FILE"
systemctl start "$SERVICE"

if wait_state RecoveryPending 120; then
  report PASS "Credential loss enters RecoveryPending" "RegistrationId=$(registration_id)"
else
  report FAIL "Credential loss enters RecoveryPending" "Hello did not request recovery"
  exit 1
fi

if approve_current; then
  if [[ "$(registration_id)" == "$original_registration" && "$(installation_id)" == "$original_installation" ]]; then
    report PASS "Recovery preserves identity" "RegistrationId and InstallationId unchanged"
  else
    report FAIL "Recovery preserves identity" "identity changed during recovery"
    exit 1
  fi
else
  report FAIL "Recovery approval" "Hello failed to return to Registered"
  exit 1
fi

# 2. Clone/conflict handling: modify only InstallationId while retaining the valid credential.
conflict_backup="$(mktemp)"
cp -a "$IDENTITY_FILE" "$conflict_backup"
restore_conflict_identity() {
  if [[ -f "$conflict_backup" ]]; then
    systemctl stop "$SERVICE" >/dev/null 2>&1 || true
    cp -a "$conflict_backup" "$IDENTITY_FILE"
    chmod 600 "$IDENTITY_FILE"
    systemctl start "$SERVICE" >/dev/null 2>&1 || true
    rm -f "$conflict_backup"
  fi
}
trap restore_conflict_identity EXIT

systemctl stop "$SERVICE"
python3 - "$IDENTITY_FILE" <<'PY'
import json,sys
p=sys.argv[1]
with open(p,encoding="utf-8") as f:
    d=json.load(f)
d["installationId"]=(d.get("installationId") or "unknown") + "-acceptance-clone"
with open(p,"w",encoding="utf-8") as f:
    json.dump(d,f,indent=2)
    f.write("\n")
PY
chmod 600 "$IDENTITY_FILE"
systemctl start "$SERVICE"

if wait_state IdentityConflict 120; then
  report PASS "Clone identity conflict detected" "wrong InstallationId rejected"
else
  report FAIL "Clone identity conflict detected" "Hello did not enter IdentityConflict"
  exit 1
fi

ops_action "$original_registration" conflict/retain-existing "Acceptance test: retain authoritative installation" >/dev/null
systemctl stop "$SERVICE"
cp -a "$conflict_backup" "$IDENTITY_FILE"
chmod 600 "$IDENTITY_FILE"
rm -f "$conflict_backup"
trap - EXIT
systemctl start "$SERVICE"

if wait_state Registered 120; then
  report PASS "Conflict retain-existing recovery" "authoritative identity authenticates again"
else
  report FAIL "Conflict retain-existing recovery" "Hello did not return to Registered"
  exit 1
fi

# 3. Revoke then purge; client must preserve InstallationId and create a fresh Pending record.
old_registration="$(registration_id)"
ops_action "$old_registration" revoke "Acceptance test revoke" >/dev/null
if wait_state Revoked 120; then
  report PASS "Revocation enforced" "Hello reports Revoked"
else
  report FAIL "Revocation enforced" "Hello did not report Revoked"
  exit 1
fi

ops_purge "$old_registration" "Acceptance test purge after revoke" >/dev/null
if wait_state Pending 180; then
  new_registration="$(registration_id)"
  if [[ -n "$new_registration" && "$new_registration" != "$old_registration" && "$(installation_id)" == "$original_installation" ]]; then
    report PASS "Purge creates fresh Pending" "new RegistrationId=$new_registration; InstallationId preserved"
  else
    report FAIL "Purge creates fresh Pending" "registration or installation identity did not transition correctly"
    exit 1
  fi
else
  report FAIL "Purge creates fresh Pending" "Hello did not return to Pending"
  exit 1
fi

# 4. Reject the fresh pending request, then purge it and allow one final clean registration.
rejected_registration="$(registration_id)"
ops_action "$rejected_registration" reject "Acceptance test rejection" >/dev/null
if wait_state Rejected 120; then
  report PASS "Pending rejection enforced" "Hello reports Rejected"
else
  report FAIL "Pending rejection enforced" "Hello did not report Rejected"
  exit 1
fi

ops_purge "$rejected_registration" "Acceptance test cleanup after rejection" >/dev/null
if wait_state Pending 180; then
  final_pending="$(registration_id)"
  if [[ "$final_pending" != "$rejected_registration" ]]; then
    report PASS "Rejected purge allows new request" "new RegistrationId=$final_pending"
  else
    report FAIL "Rejected purge allows new request" "RegistrationId was not replaced"
    exit 1
  fi
else
  report FAIL "Rejected purge allows new request" "Hello did not create another Pending request"
  exit 1
fi

if approve_current; then
  report PASS "Final approval restores service" "Hello is Registered"
else
  report FAIL "Final approval restores service" "Hello failed final registration"
  exit 1
fi

if [[ "$(installation_id)" == "$original_installation" ]]; then
  report PASS "InstallationId invariant" "$original_installation"
else
  report FAIL "InstallationId invariant" "InstallationId changed"
fi

printf '%s\n' "------------------------------------------------------------------------------------------------"
printf 'PASS=%d FAIL=%d\n' "$pass" "$fail"

(( fail == 0 ))
