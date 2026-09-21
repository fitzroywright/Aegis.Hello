# Aegis.Hello

Reference application for proving the Aegis control-plane contracts on a real deployed workload.

## Common.Secrets reference configuration

Hello is the reference consumer for the provider-instance Common.Secrets contract.

Production configuration uses a named provider instance:

```json
{
  "CommonSecrets": {
    "Mode": "Production",
    "ProviderOrder": [ "PrimarySecrets" ],
    "Providers": {
      "PrimarySecrets": {
        "Type": "OpenBao",
        "Settings": {
          "Enabled": true,
          "Address": "https://openbao.ffpja.org",
          "MountPath": "secret",
          "BasePath": "aegis/hello",
          "RequireHttps": true,
          "SecretIdEnvironmentVariable": "AEGIS_HELLO_OPENBAO_SECRET_ID"
        }
      }
    }
  }
}
```

The provider instance name (`PrimarySecrets`) is application/deployment vocabulary.
`ProviderOrder` references that name. Common.Secrets dispatches the provider by its
`Type`; only the OpenBao implementation interprets the nested `Settings`.

The Secret Zero credential itself is not stored in appsettings. For this reference
deployment the AppRole Secret ID is supplied externally through
`AEGIS_HELLO_OPENBAO_SECRET_ID` (or a protected file if configured operationally).

There is intentionally no legacy `CommonSecrets:OpenBao` fallback.

## Public URL

`Aegis:PublicUrl` is the human/browser-facing URL for Hello. It is appropriate here
because Hello exposes a public endpoint. Applications without a public/browser endpoint
do not need to declare it.

## Build

```powershell
dotnet restore
dotnet build
dotnet test
```
