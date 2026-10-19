# Kite .NET Payments

ASP.NET Core x402 seller middleware and a bounded JSON proxy.

Version 0.1.0 is an AI-assisted engineering evaluation release, developed under one operator and assigned to this GitHub account. It is not evidence of independent human adoption, official Kite approval, Electric Capital inclusion or successful live payment.

## Run and verify

.NET 8 SDK is required.

```sh
dotnet build examples/Proxy
dotnet run --project tests/ContractTests
dotnet pack src/Kite.Payments -c Release -o artifacts
dotnet run --project examples/Proxy --urls http://127.0.0.1:4401
```

The package exposes AddKitePayments(options), UseKitePayments(), Prices.Units() and Journal. POST /v1/sample/query proxies one configured HTTPS JSON upstream; /healthz is free. Unsupported streaming and non-JSON upstream results fail before settlement. Upstream auth is separately configured, never copied from buyer headers. InspectSupportAsync() performs a read-only /supported capability check.

The journal encrypts results and redacted payment metadata with AES-256-GCM. A file lock and in-process gate serialize operations. Verification/settlement uncertainty is durable. Repeated POST returns 409; the sample's operator-authorized /admin/operations/{id} recovers the original stored result. Buyer self-service SIWX recovery is outside this .NET release; the operator must validate customer ownership before forwarding an artifact.

NuGet packaging is implemented and installable from a local feed. Publishing to nuget.org is not part of this GitHub release. This reliability-focused component is not claimed to be the first .NET x402 implementation.

## Configuration and recovery

Use .env.example as a list of required environment variables; the service does not automatically load it. Generate a fresh 32-byte hex DATA_KEY locally, keep it outside the repository and retain it across restarts. Supply strong operator/application bearer tokens and a real receiver and asset before enabling a service. Values are intentionally blank so example configuration cannot silently charge a wrong wallet. The stored payment state UNKNOWN never means unpaid. Inspect the original receipt/activity and restricted journal; do not change an operation key to buy again. Where a writer lock survives a crash, stop all writer processes and review the journal before removing only the stale lock directory.

## Integration evidence

Read [compatibility.json](compatibility.json) and [the protocol boundary](docs/PROTOCOL.md). Local tests are E1; read-only public capability/RPC observations prove only the facts observed. Real Passport approval, funded purchase, SIWX interoperability, public deployment and catalog/EC review are not completed by this release. The service manifest stays draft and uses placeholder pay_to until a genuine receiver and deployment are configured.

## Scope and evidence

[Original PRD](docs/PRD.md) describes the full target, not a claim that every external acceptance gate has passed. [Event contract](docs/EVENTS.md) defines the application records. The standalone repositories share protocol/storage support code, with distinct business implementations and tests. The license is MIT; third-party dependencies keep their upstream licenses. Reproduce failures without publishing payment signatures, account credentials or private files.
