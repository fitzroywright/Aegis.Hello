# Aegis.Hello Operational Runbook

## Purpose
Aegis.Hello is the canonical reference application for Aegis application integration and registration lifecycle behavior.

## Dependencies
- Aegis.Configuration for registration and configuration-contract publication.
- Aegis.Diagnostics for optional diagnostic reachability checks.
- Common.Registration for durable application identity and lifecycle.
- Common.Secrets for provider-neutral secret health.
- Common.Diagnostics for the standard health endpoint.

## Startup
1. Load application configuration.
2. Initialize Common.Secrets.
3. Start the HTTP application.
4. Load or create the durable registration identity.
5. Execute one registration lifecycle step.
6. When Registered, publish the metadata-only configuration contract.
7. Continue reconciliation without creating duplicate pending registrations.

## Shutdown
Stop the service normally. Do not delete the registration identity during ordinary shutdown.

## Configuration
Required settings are published to Aegis.Configuration by name and readiness state. Values are not exposed by diagnostics.

## Registration
The registration identity file is durable state. Normal restarts reuse the same InstallationId, RegistrationId and durable credential.

## Secrets
Secret values must never be written to status endpoints, logs, configuration contracts, diagnostics evidence or runbooks.

## Health Verification
- GET /health
- GET /status.json
- GET /config-state

A successful HTTP response is not by itself proof that dependencies are healthy.

## Expected Diagnostics
Hello exposes process health and can probe Aegis.Diagnostics reachability. Missing or unavailable remote evidence must not be reported as Healthy.

## Normal State
- Registration: Registered
- Configuration contract: last publication succeeded
- Stable InstallationId across restarts
- No credential or claim-token values exposed

## Common Warnings
- Pending: initial approval is required.
- RecoveryPending: durable credential is missing/invalid and recovery has been requested.
- Configuration publication unavailable: application remains operational and retries.

## Common Failures
- Revoked: administrative intervention is required.
- IdentityConflict: another installation claims the logical identity.
- Invalid configuration URL: lifecycle cannot contact Configuration.

## Recovery
Use the lifecycle acceptance scripts and administrative registration actions. Prefer reconciliation/recovery over deleting local identity.

## Backup
Back up the protected registration identity file and the authoritative Configuration registration database according to deployment policy.

## Restore
Restore authoritative registration data and the matching local identity together when possible. Verify authentication before declaring recovery complete.

## Deployment
Deploy application binaries/configuration without replacing the durable registration identity file.

## Rollback
Roll back binaries while preserving registration identity and durable state.

## Purge
Purge is destructive administrative action. It removes the central registration and should only be used intentionally.

## Re-registration
After a legitimate purge, Hello preserves InstallationId and creates one fresh Pending registration. It must not create a new Pending record on each restart.

## Escalation
Escalate Revoked, IdentityConflict, repeated recovery failure, or database restore failure to the platform engineer.

## Log Locations
Use the service manager/journal for runtime logs and /status.json for safe lifecycle state. Never log credentials or claim tokens.
