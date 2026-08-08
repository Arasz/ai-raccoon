# .NET SDK exception-hierarchy probes (catch-chain review)

When a try/catch chain maps exceptions from an external SDK (e.g.
`catch (AmazonClientException)` → auth-failed, then `catch (AmazonServiceException)` →
forbidden), correctness depends entirely on the REAL hierarchy of the restored
package versions. The compiler only catches one failure mode; everything else
needs a probe.

## 1. Compile signal (necessary, not sufficient)

CS0160 — "A previous catch clause already catches all exceptions of this or of
a super type" — fires when a LATER catch's type derives from an EARLIER catch's
type in the same try. Build 0 errors ⇒ no parent→child shadowing. It says
NOTHING about the sibling direction: if you assume siblings and they are
parent→child, the code compiles and the earlier catch silently swallows the
later one's cases (misrouted error buckets, dead mappings).

## 2. Reflection probe (ground truth)

Throwaway console project in /tmp, referencing the TOP-LEVEL packages only
(see §4):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="AWSSDK.S3" Version="<exact version from Directory.Packages.props>" />
    <PackageReference Include="Azure.Identity" Version="<exact version>" />
  </ItemGroup>
</Project>
```

```csharp
var t = Type.GetType("Amazon.Runtime.AmazonServiceException, AWSSDK.Core");
Console.WriteLine($"{t.FullName} : {t.BaseType?.FullName}");
```

`Type.GetType` needs the right assembly name: `Amazon.S3.*` → AWSSDK.S3;
`Amazon.Runtime.*` → AWSSDK.Core; `Azure.Identity.*` → Azure.Identity;
`Azure.*` → Azure.Core. Print the BaseType chain of every type the catch
clauses name, plus their declared dependencies if reachable.

## 3. Live behavior probe (what actually surfaces)

Mapping correctness is about what the SDK throws in the field, not just the
type graph. Dead-port probe pattern — port 9 (discard) refuses instantly, no
network dependency:

```csharp
// chain mode, no creds configured anywhere:
var client = new AmazonS3Client(new AmazonS3Config { ServiceURL = "http://127.0.0.1:9", ForcePathStyle = true });
await client.GetObjectAsync("bucket", "key"); // observe exception type
// with fake creds against the dead endpoint:
new AmazonS3Client("AKIAFAKEKEY123", "fake", config);
```

Also print `ex.InnerException` — the inner type identifies the layer (e.g.
`SocketException` under `HttpRequestException`).

## 4. Pitfalls

- **The NuGet global cache is not a restore source.** A version present in
  `~/.nuget/packages` but not on nuget.org fails restore with NU1102. Reference
  the top-level package and let transitive deps resolve, or HintPath straight
  to the cached DLLs (`lib/<tfm>/*.dll`).
- **Pinned ≠ effective.** The version in Directory.Packages.props is the
  top-level version; the effective restored version of a transitive dep can
  differ. Observed: AWSSDK.S3 4.0.101.7 restores AWSSDK.Core 4.0.100.9. Probe
  `obj/project.assets.json` for the ground-truth versions.
- **A passing canned-transport test also proves propagation.** A test whose
  handler throws `CredentialUnavailableException` passing means the SDK
  surfaces that type unwrapped on that path (Azure.Core's HttpClientTransport
  does not wrap non-HTTP handler exceptions). What it does NOT prove is the
  SDK's own trigger behavior (that DefaultAzureCredential throws it on a
  credential-less machine) — that needs a real-machine probe or an explicit
  "SDK behavior, probe-verified" note in the review.
- **DispatchProxy seam.** `DispatchProxy.Create<IAmazonS3, T>()` makes a
  throwing stub for any interface — the clean seam for mapping tests when the
  real object can't be faked.

## 5. Observed facts (2026-08-05, AWSSDK.S3 4.0.101.7 / AWSSDK.Core 4.0.100.9 / Azure.Identity 1.21.0)

- `AmazonClientException : Exception` and `AmazonServiceException : Exception`
  — SIBLINGS. `AmazonS3Exception : AmazonServiceException`.
- Azure.Identity: `CredentialUnavailableException : AuthenticationFailedException : Exception`.
  Azure.Core `RequestFailedException : Exception` — sibling of both.
- S3 chain mode resolves lazily: ctor succeeds with no credentials; the FIRST
  call throws `AmazonClientException` ("Failed to resolve AWS credentials"),
  before any network I/O.
- Connection refused (creds present, dead endpoint) → `System.Net.Http.HttpRequestException`
  (inner `SocketException`) — map to network, not auth.
- Consequence for S3 catch ordering that works: NotFound/PreconditionFailed
  (`AmazonS3Exception`) FIRST, then `AmazonClientException`, then
  `AmazonServiceException`-when-Forbidden, then generic `AmazonS3Exception`,
  then `HttpRequestException`.
