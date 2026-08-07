# Encryption-at-Rest Patterns for .NET Memory Stores

**Research Date**: 2026-08-04  
**Project Context**: AiRaccoon — C# .NET 10 MCP server on sqlite-memory (macOS dev, local-first)  
**Sources**: Microsoft Learn (ASP.NET Core 10 / EF Core 11 docs), Zetetic SQLCipher docs, dotnet/runtime source

---

## 1. Windows DPAPI / macOS Keychain Integration Patterns

### 1.1 Windows: `System.Security.Cryptography.ProtectedData`

**Windows-only.** Calls into the OS DPAPI (`CryptProtectData`/`CryptUnprotectData`). Throws `PlatformNotSupportedException` on macOS/Linux.

```csharp
// NuGet: System.Security.Cryptography.ProtectedData
using System.Security.Cryptography;

// Encrypt — tied to current user (cannot be decrypted by another user or on another machine)
byte[] encrypted = ProtectedData.Protect(
    Encoding.UTF8.GetBytes("secret"),
    optionalEntropy: null,            // or a static byte[] salt
    DataProtectionScope.CurrentUser);  // or LocalMachine

// Decrypt
byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
```

**Pitfalls**:
- Not cross-platform — `PlatformNotSupportedException` on non-Windows
- `CurrentUser` scope: only the same user on the same machine can decrypt. Machine re-image = data loss
- `LocalMachine` scope: any process on the machine can decrypt — weak isolation
- No key rotation, no scoping per app/purpose

**Verdict for AiRaccoon**: ❌ macOS-primary project. Not viable.

### 1.2 macOS: `security` CLI (Keychain)

macOS provides the `security` command-line tool for Keychain operations:

```bash
# Store a DB encryption key
security add-generic-password -s "ai-raccoon" -a "memory.db" -w "base64-encoded-aes-key"

# Retrieve it
security find-generic-password -s "ai-raccoon" -a "memory.db" -w
```

From .NET, invoke via `Process.Start`:

```csharp
public static string GetKeychainSecret(string service, string account)
{
    var psi = new ProcessStartInfo
    {
        FileName = "/usr/bin/security",
        Arguments = $"find-generic-password -s \"{service}\" -a \"{account}\" -w",
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true
    };
    using var proc = Process.Start(psi)!;
    var result = proc.StandardOutput.ReadToEnd().Trim();
    proc.WaitForExit();
    return proc.ExitCode == 0 ? result : throw new InvalidOperationException("Keychain lookup failed");
}
```

**Pitfalls**:
- First access pops a GUI confirmation dialog ("security wants to use your keychain") — bad for headless/daemon
- Can be avoided with `-T /path/to/app` at store time to pre-authorize a specific binary
- Keychain syncs via iCloud across devices if enabled — may leak secrets unintentionally
- Not suitable for programmatic key material without the pre-authorization dance

**macOS native API**: `Security` namespace (`SecKeyChain`) is available only in Xamarin/Mac Catalyst/MAUI targets, not in vanilla .NET console/server apps.

### 1.3 Cross-Platform Abstraction: ASP.NET Core Data Protection

The `Microsoft.AspNetCore.DataProtection` stack provides a unified API that works on Windows, macOS, and Linux. See Section 3 for details. This is the recommended cross-platform key-wrapping layer.

---

## 2. SQLite-Based Encryption-at-Rest

### 2.1 The Landscape

SQLite core has **no built-in encryption**. You need a modified build:

| Solution | License | .NET Support | Notes |
|---|---|---|---|
| **SQLite3 Multiple Ciphers** (e_sqlite3mc) | MIT | ✅ Bundled in `Microsoft.Data.Sqlite` 11.0+ | Superset: works unencrypted by default, encrypts when `Password` is set |
| **SQLCipher** (Zetetic) | BSD-style / Commercial | ✅ Via `SQLitePCLRaw.provider.sqlcipher` | Paid builds available; open-source self-build possible |
| **SEE** (SQLite Encryption Extension) | Paid, $2000 one-time | ✅ Via SourceGear's SQLitePCLRaw builds | Official from SQLite team |
| **wxSQLite3** | LGPL | Partial | Less common in .NET |

### 2.2 SQLite3 Multiple Ciphers (The Default in .NET 10 / EF Core 11)

Starting with `Microsoft.Data.Sqlite` 11.0, the package bundles `SQLite3MC.PCLRaw.bundle` (e_sqlite3mc). **Encryption works out of the box** — no extra packages needed.

```csharp
// New database with encryption
var csb = new SqliteConnectionStringBuilder
{
    DataSource = "memory.db",
    Mode = SqliteOpenMode.ReadWriteCreate,
    Password = "your-secure-passphrase"
};
using var connection = new SqliteConnection(csb.ToString());
connection.Open();

// Change key
using var cmd = connection.CreateCommand();
cmd.CommandText = "SELECT quote($newPassword)";
cmd.Parameters.AddWithValue("$newPassword", newPassword);
var quoted = (string)cmd.ExecuteScalar()!;
cmd.CommandText = $"PRAGMA rekey = {quoted}";
cmd.Parameters.Clear();
cmd.ExecuteNonQuery();

// Open existing SQLCipher database (legacy format)
var csb = new SqliteConnectionStringBuilder
{
    // URI format needed for cipher compatibility parameters
    DataSource = "file:memory.db?cipher=sqlcipher&legacy=4",
    Password = "your-passphrase"
};
```

### 2.3 Current AiRaccoon State

The project currently uses:
- `SQLitePCLRaw.bundle_e_sqlite3` — **no encryption support**
- `Microsoft.Data.Sqlite` (which in 11.0+ bundles e_sqlite3mc) — **but the explicit bundle_e_sqlite3 override may conflict**

**Path to encryption**:
1. Remove `SQLitePCLRaw.bundle_e_sqlite3` (redundant with Microsoft.Data.Sqlite 11.0+)
2. Add `Password` support to `SqliteConnectionFactory.OpenBankAsync()`
3. Store the passphrase via Data Protection API (Section 3) or macOS Keychain

### 2.4 Concrete Integration Pattern for AiRaccoon

```csharp
// SqliteConnectionFactory.cs — updated OpenBankAsync
public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
{
    Directory.CreateDirectory(BankDirectory);

    var csb = new SqliteConnectionStringBuilder
    {
        DataSource = BankPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // Only set password if encryption is configured
        Password = _options.EncryptionKey   // from IEncryptionKeyProvider
    };

    var connection = new SqliteConnection(csb.ToString());
    await OpenWithPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
    // ... rest unchanged
}
```

**Critical**: If `Password` is set on an existing *unencrypted* database, SQLite3 Multiple Ciphers will fail to open it. You must migrate:
```sql
-- Migrate unencrypted → encrypted
ATTACH DATABASE 'encrypted.db' AS encrypted KEY 'new-password';
SELECT sqlcipher_export('encrypted');
DETACH DATABASE encrypted;
```

---

## 3. ASP.NET Core Data Protection API

### 3.1 Core API

```csharp
// NuGet: Microsoft.AspNetCore.DataProtection.Abstractions
using Microsoft.AspNetCore.DataProtection;

// Create a protector scoped to "ai-raccoon/memory-store"
var protector = provider.CreateProtector("ai-raccoon", "memory-store");

string plaintext = "sensitive observation data";
string ciphertext = protector.Protect(plaintext);
string recovered  = protector.Unprotect(ciphertext); // throws CryptographicException if tampered
```

### 3.2 Purpose Strings (Scoping)

Purpose strings provide cryptographic isolation. A protector created with `("app-A", "users")` cannot decrypt data protected by `("app-B", "users")`.

**AiRaccoon scoping pattern**:
```csharp
// Per-project isolation
var projectProtector = provider.CreateProtector("ai-raccoon", $"project:{projectId}");

// Per-workspace
var workspaceProtector = provider.CreateProtector("ai-raccoon", $"workspace:{workspaceId}");
```

### 3.3 Key Management

- **Automatic key rotation**: Default key lifetime is 90 days. Old keys stay for decryption; new keys generated for encryption.
- **Key ring storage**: Filesystem (`%LOCALAPPDATA%/ASP.NET/DataProtection-Keys` on Windows, `~/.aspnet/DataProtection-Keys` on macOS), EF Core, Azure, custom `IXmlRepository`
- **Key encryption at rest**: DPAPI (Windows only), X.509 certificate (cross-platform), Azure Key Vault, custom `IXmlEncryptor`
- **Key ring refresh**: Checked every 24 hours or on key expiry

### 3.4 Non-DI / Console App Setup

Not an ASP.NET app? Use `DataProtectionProvider` directly:

```csharp
using Microsoft.AspNetCore.DataProtection;

var provider = DataProtectionProvider.Create(
    new DirectoryInfo(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ai-raccoon", "keys")));
var protector = provider.CreateProtector("ai-raccoon", "memory-store");
```

Or with DI in a console app:
```csharp
var host = Host.CreateDefaultBuilder()
    .ConfigureServices((ctx, services) =>
    {
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
            .SetApplicationName("ai-raccoon")
            // On macOS: protect key ring with X.509 cert
            .ProtectKeysWithCertificate(cert);
    })
    .Build();
```

### 3.5 Fit for AiRaccoon Memory Store

| Aspect | Assessment |
|---|---|
| **Key rotation** | ✅ Automatic, 90-day default. Keys never deleted, only expired → old data always decryptable |
| **Scoping** | ✅ Purpose strings give per-project/workspace isolation |
| **Cross-platform** | ✅ Works on macOS, Windows, Linux |
| **Non-ASP.NET** | ✅ `DataProtectionProvider.Create()` works without ASP.NET |
| **Long-term persistence** | ⚠️ Microsoft docs say: "not primarily intended for indefinite persistence." CNG DPAPI better for decades-scale storage. But keys are never removed, so practically it works |
| **Key storage on macOS** | ⚠️ Default: keys stored unencrypted on disk. Must use `ProtectKeysWithCertificate()` for encrypted key ring |
| **Dependency weight** | Light: `Microsoft.AspNetCore.DataProtection.Abstractions` + `Microsoft.AspNetCore.DataProtection` |

**Recommendation**: Use ASP.NET Core Data Protection to **protect the SQLite encryption passphrase**, not every row. The passphrase is short-lived per process session; the key ring handles rotation.

```csharp
// IEncryptionKeyProvider — resolves the SQLite passphrase
public interface IEncryptionKeyProvider
{
    string? GetPassphrase(); // null = encryption disabled
}

// DataProtection-backed implementation
public class DataProtectionKeyProvider : IEncryptionKeyProvider
{
    private readonly IDataProtector _protector;
    private string? _cached;

    public DataProtectionKeyProvider(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("ai-raccoon", "sqlite-passphrase");
    }

    public string? GetPassphrase()
    {
        if (_cached is not null) return _cached;

        var env = Environment.GetEnvironmentVariable("AIRACCOON_DB_PASSPHRASE");
        if (env is not null) return _cached = env;

        var protectedPath = Path.Combine(bankDir, ".passphrase");
        if (File.Exists(protectedPath))
        {
            var protected = File.ReadAllText(protectedPath);
            return _cached = _protector.Unprotect(protected);
        }

        return null; // encryption disabled
    }
}
```

---

## 4. Performance/Cost Tradeoffs

### 4.1 Transparent Encryption (SQLCipher / e_sqlite3mc)

```
App Code → SQL Query → SQLite → [Encrypt/Decrypt per 4KB page] → Disk
```

| Metric | Value |
|---|---|
| **Read overhead** | 5–10% (AES-256-CBC decrypt per page on read) |
| **Write overhead** | 5–15% (AES-256-CBC encrypt per page + HMAC on write) |
| **Bulk insert** | Minimized by using transactions (amortize page writes) |
| **Full table scan** | Nearly same as plain SQLite (per-page decrypt is fast) |
| **Index performance** | Unchanged — indexes work identically |
| **Code changes** | **Zero application code changes.** Just set `Password` in connection string |
| **Key derivation** | SQLCipher uses PBKDF2 (64K iterations) on first open — expect 50-100ms one-time cost |

**Key insight**: SQLCipher encrypts at the page level (4096 bytes), not per-row or per-column. `LIKE`, `WHERE`, `ORDER BY`, and FTS5 all work normally because the data is decrypted transparently in the page cache.

### 4.2 Explicit Encryption (Per-Column AES-GCM)

```
App Code → Encrypt value → Store blob → Decrypt on read → App Code
SQL can't filter/sort/index encrypted columns
```

| Metric | Value |
|---|---|
| **Per-value overhead** | AES-256-GCM: ~1-2μs per encrypt + 12-byte nonce + 16-byte tag |
| **Storage overhead** | Nonce (12B) + Tag (16B) + ciphertext per row — stored as BLOB or Base64 |
| **FTS5 / Search** | ❌ **Broken** — encrypted text cannot be full-text indexed |
| **WHERE filtering** | ❌ **Broken** — SQL can't compare encrypted values |
| **ORDER BY** | ❌ **Broken** — encrypted ordering is meaningless |
| **vec0 similarity** | ❌ **Broken** — embeddings stored as encrypted blobs can't be compared |
| **Code changes** | 🔴 **Substantial** — every read/write path must encrypt/decrypt |
| **Selective encryption** | ✅ Can encrypt only sensitive columns, leave others plaintext |

### 4.3 AiRaccoon-Specific Analysis

The AiRaccoon memory store relies on:
- **FTS5** full-text search → must have plaintext
- **vec0** vector similarity → must have plain embeddings
- **WHERE** filtering by project, scope, hash, context → scanning works on encrypted DB since decryption is transparent

**Clear winner: Transparent (SQLCipher/e_sqlite3mc)**
- FTS5 and vec0 work because page-level decryption is transparent
- Zero application code changes
- 5-15% overhead is acceptable for a memory store (not a high-throughput OLTP system)
- AiRaccoon already uses `Microsoft.Data.Sqlite` 11.0+ which bundles e_sqlite3mc

### 4.4 Migration Strategy for AiRaccoon

```
Phase 1: Enable encryption on new databases only (opt-in via config/env)
Phase 2: Provide migration tool (sqlcipher_export) for existing memory.db
Phase 3: Make encryption default, require explicit opt-out
```

**Phase 1 implementation** (minimal changes):
1. Add `InfrastructureOptions.EncryptionPassphrase` (nullable — null = no encryption)
2. In `SqliteConnectionFactory.OpenBankAsync()`, conditionally set `Password`
3. Passphrase resolved from env var or macOS Keychain
4. Existing unencrypted DBs continue to work (passphrase is null)

No FTS5 changes, no query rewrites needed.

---

## 5. Recommended Package Changes

For AiRaccoon `.csproj`:

```diff
- <PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3"/>
  <!-- Remove: Microsoft.Data.Sqlite 11.0+ already bundles e_sqlite3mc -->
  
+ <PackageReference Include="Microsoft.AspNetCore.DataProtection.Abstractions"/>
  <!-- For IDataProtectionProvider / IDataProtector -->
  
+ <PackageReference Include="Microsoft.AspNetCore.DataProtection"/>
  <!-- For DataProtectionProvider.Create() in console app -->
```

The `Microsoft.Data.Sqlite` package already brings `SQLite3MC.PCLRaw.bundle` which supports encryption when `Password` is set. Remove the explicit `bundle_e_sqlite3` reference.

---

## 6. Summary of Recommendations

| Layer | Recommendation | Rationale |
|---|---|---|
| **Data encryption** | Transparent SQLite encryption (e_sqlite3mc) via `Password` in connection string | Zero code changes, FTS5/vec0 work, 5-15% overhead |
| **Key storage (macOS)** | macOS Keychain via `security` CLI for dev; X.509 certificate for production | Secure OS-backed storage with ACL support |
| **Key wrapping** | ASP.NET Core Data Protection API to protect the passphrase at rest | Cross-platform, automatic key rotation, purpose-based scoping |
| **Cross-platform key storage (future Windows/Linux)** | Data Protection with X.509 cert on macOS; DPAPI on Windows | Unified API, platform-optimal storage per OS |
| **Migration** | `sqlcipher_export` for existing DBs; opt-in first, default later | Safe rollout path |

### Architecture Diagram

```
┌─────────────────────────────────────────────────────┐
│                   AiRaccoon App                      │
│  IMemoryStore → SqliteMemoryStore                    │
│       │                                              │
│       ▼                                              │
│  SqliteConnectionFactory                             │
│    ├─ IEncryptionKeyProvider.GetPassphrase()         │
│    │     │                                           │
│    │     ├─ macOS Keychain (dev machine)             │
│    │     ├─ Env var (AIRACCOON_DB_PASSPHRASE)        │
│    │     └─ ASP.NET Data Protection (wrapped file)   │
│    │                                                │
│    └─ SqliteConnectionStringBuilder                  │
│          Password = passphrase   ← enables AES-256   │
│          DataSource = memory.db                      │
└─────────────────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────┐
│  SQLite3 Multiple Ciphers (e_sqlite3mc)              │
│    • AES-256-CBC per 4096-byte page                  │
│    • HMAC-SHA1 integrity per page                    │
│    • PBKDF2-HMAC-SHA1 key derivation (64K rounds)    │
│    • FTS5, vec0, indexes — all transparent           │
└─────────────────────────────────────────────────────┘
                          │
                          ▼
                   memory.db (encrypted on disk)
```
