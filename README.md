# Aegis.Hello

Aegis.Hello is the minimal reference implementation for integrating an Aegis application correctly.

## Reference lifecycle

The expected order is:

1. Load configuration.
2. Initialize Common.Secrets.
3. Start the application.
4. Load/create the durable Common.Registration identity.
5. Reconcile registration with Aegis.Configuration.
6. If Registered, publish the configuration contract.
7. Expose Common.Diagnostics health and safe lifecycle status.
8. Retry central-control-plane operations without making application availability depend on them.

Normal restarts reuse the same durable identity and **must not create a new pending registration**.

## Registration acceptance

Use `scripts/acceptance-smoke.sh` for non-destructive checks.

Use `scripts/acceptance-lifecycle-destructive.sh` only with explicit authorization. It verifies credential-loss recovery, identity conflict, revoke, purge, re-registration, final approval and InstallationId preservation.

The production gate combines smoke, reconciliation, database restore verification, optional destructive lifecycle testing and a runtime secret-leakage scan.

## Operational documentation

See `docs/RUNBOOK.md`.
