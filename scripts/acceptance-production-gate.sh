#!/usr/bin/env bash
set -euo pipefail

HELLO_URL="${AEGIS_HELLO_URL:-http://127.0.0.1:5400}"
OPERATIONS_URL="${AEGIS_OPERATIONS_URL:-https://operations.ffpja.org}"
COOKIE_JAR="${AEGIS_OPERATIONS_COOKIE_JAR:-}"
SMOKE_SCRIPT="${AEGIS_HELLO_SMOKE_SCRIPT:-/opt/aegis/hello/scripts/acceptance-smoke.sh}"
DESTRUCTIVE_SCRIPT="${AEGIS_HELLO_DESTRUCTIVE_SCRIPT:-/opt/aegis/hello/scripts/acceptance-lifecycle-destructive.sh}"
BACKUP_RESTORE_SCRIPT="${AEGIS_REGISTRATION_BACKUP_RESTORE_SCRIPT:-/opt/aegis/configuration/scripts/verify-registration-backup-restore.sh}"
RUN_DESTRUCTIVE="${AEGIS_ACCEPTANCE_DESTRUCTIVE:-NO}"

pass=0
fail=0
blocked=0

report() {
  local state="$1" test_name="$2" evidence="$3"
  printf '%-8s | %-38s | %s\n' "$state" "$test_name" "$evidence"
  case "$state" in
    PASS) pass=$((pass+1)) ;;
    FAIL) fail=$((fail+1)) ;;
    BLOCKED) blocked=$((blocked+1)) ;;
  esac
}

capture() {
  local outfile="$1"
  shift
  set +e
  "$@" >"$outfile" 2>&1
  local rc=$?
  set -e
  return "$rc"
}

printf '%-8s | %-38s | %s\n' "RESULT" "PRODUCTION GATE" "EVIDENCE"
printf '%s\n' "----------------------------------------------------------------------------------------------------------------"

# Gate 1: Hello non-destructive host acceptance.
if [[ -x "$SMOKE_SCRIPT" ]]; then
  smoke_out="$(mktemp)"
  if capture "$smoke_out" env AEGIS_HELLO_URL="$HELLO_URL" "$SMOKE_SCRIPT"; then
    summary="$(tail -n 1 "$smoke_out" | tr '\n' ' ')"
    report PASS "Hello smoke acceptance" "${summary:-completed successfully}"
  else
    rc=$?
    summary="$(tail -n 3 "$smoke_out" | tr '\n' ' ' | cut -c1-500)"
    if [[ "$rc" -eq 2 ]]; then
      report BLOCKED "Hello smoke acceptance" "${summary:-blocked}"
    else
      report FAIL "Hello smoke acceptance" "${summary:-failed}"
    fi
  fi
  rm -f "$smoke_out"
else
  report BLOCKED "Hello smoke acceptance" "script not found/executable: $SMOKE_SCRIPT"
fi

# Gate 2: Registration reconciliation must be healthy.
if [[ -n "$COOKIE_JAR" && -f "$COOKIE_JAR" ]]; then
  reconcile_out="$(mktemp)"
  if curl -fsS -b "$COOKIE_JAR" "$OPERATIONS_URL/api/lifecycle-registrations/reconciliation" >"$reconcile_out"; then
    if python3 - "$reconcile_out" <<'PY'
import json,sys
with open(sys.argv[1], encoding="utf-8") as f:
    d=json.load(f)
raise SystemExit(0 if d.get("healthy") is True else 1)
PY
    then
      count="$(python3 - "$reconcile_out" <<'PY'
import json,sys
with open(sys.argv[1], encoding="utf-8") as f:d=json.load(f)
print(d.get("registrationCount",0))
PY
)"
      report PASS "Registration reconciliation" "$count registration(s); healthy=true"
    else
      evidence="$(python3 - "$reconcile_out" <<'PY'
import json,sys
with open(sys.argv[1], encoding="utf-8") as f:d=json.load(f)
issues=d.get("issues") or []
print("; ".join(f"{x.get('code')}:{x.get('applicationId')}/{x.get('instanceId')}" for x in issues) or "healthy=false")
PY
)"
      report FAIL "Registration reconciliation" "$evidence"
    fi
  else
    report FAIL "Registration reconciliation" "Operations reconciliation endpoint unavailable"
  fi
  rm -f "$reconcile_out"
else
  report BLOCKED "Registration reconciliation" "set AEGIS_OPERATIONS_COOKIE_JAR to an authenticated Operations cookie jar"
fi

# Gate 3: Database backup/restore verification.
if [[ -x "$BACKUP_RESTORE_SCRIPT" ]]; then
  if [[ -n "${AEGIS_REGISTRATION_PGURL:-}" && -n "${AEGIS_REGISTRATION_RESTORE_TEST_PGURL:-}" ]]; then
    restore_out="$(mktemp)"
    if capture "$restore_out" "$BACKUP_RESTORE_SCRIPT"; then
      summary="$(tail -n 4 "$restore_out" | tr '\n' ' ' | cut -c1-700)"
      report PASS "Registration DB restore" "$summary"
    else
      summary="$(tail -n 8 "$restore_out" | tr '\n' ' ' | cut -c1-700)"
      report FAIL "Registration DB restore" "$summary"
    fi
    rm -f "$restore_out"
  else
    report BLOCKED "Registration DB restore" "set AEGIS_REGISTRATION_PGURL and AEGIS_REGISTRATION_RESTORE_TEST_PGURL"
  fi
else
  report BLOCKED "Registration DB restore" "script not found/executable: $BACKUP_RESTORE_SCRIPT"
fi

# Gate 4: destructive lifecycle matrix is explicitly opt-in.
if [[ "$RUN_DESTRUCTIVE" == "YES" ]]; then
  if [[ -x "$DESTRUCTIVE_SCRIPT" ]]; then
    destructive_out="$(mktemp)"
    if capture "$destructive_out" env       AEGIS_ACCEPTANCE_DESTRUCTIVE=YES       AEGIS_HELLO_URL="$HELLO_URL"       AEGIS_OPERATIONS_URL="$OPERATIONS_URL"       AEGIS_OPERATIONS_COOKIE_JAR="$COOKIE_JAR"       "$DESTRUCTIVE_SCRIPT"; then
      summary="$(tail -n 1 "$destructive_out" | tr '\n' ' ')"
      report PASS "Destructive lifecycle matrix" "${summary:-completed successfully}"
    else
      summary="$(tail -n 8 "$destructive_out" | tr '\n' ' ' | cut -c1-700)"
      report FAIL "Destructive lifecycle matrix" "$summary"
    fi
    rm -f "$destructive_out"
  else
    report BLOCKED "Destructive lifecycle matrix" "script not found/executable: $DESTRUCTIVE_SCRIPT"
  fi
else
  report BLOCKED "Destructive lifecycle matrix" "set AEGIS_ACCEPTANCE_DESTRUCTIVE=YES when ready to exercise recovery/conflict/revoke/purge"
fi

# Gate 5: direct status leakage check, independent of the smoke script.
status_out="$(mktemp)"
if curl -fsS "$HELLO_URL/status.json" >"$status_out"; then
  if grep -Eqi '"(credential|claimToken|registrationKey)"[[:space:]]*:[[:space:]]*"[^"]+"' "$status_out"; then
    report FAIL "Runtime secret leakage scan" "Hello status endpoint appears to expose credential material"
  else
    report PASS "Runtime secret leakage scan" "no credential/claim values exposed by status.json"
  fi
else
  report FAIL "Runtime secret leakage scan" "Hello status endpoint unavailable"
fi
rm -f "$status_out"

printf '%s\n' "----------------------------------------------------------------------------------------------------------------"
printf 'PASS=%d FAIL=%d BLOCKED=%d\n' "$pass" "$fail" "$blocked"

if (( fail > 0 )); then
  exit 1
fi
if (( blocked > 0 )); then
  exit 2
fi
exit 0
