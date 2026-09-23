namespace AiRaccoon;

/// <summary>
///     The CLI's exit codes (ADR-0107): two digits, the tens digit the category and the ones digit
///     the case; <c>x0</c> is each category's most general case. Only <see cref="Ok" /> sits outside 10-99.
/// </summary>
public static class ErrorCode
{
    public static class Ok
    {
        /// <summary>The command did what it was asked.</summary>
        public const int Success = 0;

        /// <summary>Cancelled by Ctrl-C or SIGTERM before it finished (128 + SIGINT); the command changed nothing.</summary>
        public const int SIGC = 130;
    }

    /// <summary>The argv, a value, or an interactive answer is wrong; fix the invocation.</summary>
    public static class Usage
    {
        /// <summary>A value fits its slot in the grammar but is invalid: bad enum, non-number, out of range, removed option value.</summary>
        public const int InvalidValue = 10;

        /// <summary>The argv does not fit the grammar: unknown or misplaced token, extra token, missing subcommand.</summary>
        public const int Unparseable = 11;

        /// <summary>A required value is absent: a required argument, an empty interactive secret, or --account with --cli.</summary>
        public const int MissingValue = 12;

        /// <summary>--port is 0 or outside 1-65535 on a path that must dial a fixed port.</summary>
        public const int UndialablePort = 13;

        /// <summary>--transport names a removed value (stdio, https).</summary>
        public const int RemovedTransport = 14;

        /// <summary>repair project-ids --map names a file that is missing, unreadable or not a valid alias map.</summary>
        public const int AliasMapInvalid = 15;

        /// <summary>A model download over the size guard was not confirmed; nothing was touched.</summary>
        public const int ConfirmationDeclined = 16;

        /// <summary>The server rejected the request as malformed (HTTP 400) for a reason the CLI pre-flight did not catch.</summary>
        public const int RequestRejected = 17;
    }

    /// <summary>The encryption key cannot be resolved, is wrong, or its source (env, Bitwarden CLI, sidecar) is unusable.</summary>
    public static class Key
    {
        /// <summary>No encryption key could be resolved; the most general key failure.</summary>
        public const int Unresolved = 20;

        /// <summary>The resolved key does not open the bank: corrupt, or keyed to a different secret.</summary>
        public const int WrongKey = 21;

        /// <summary>The bank is still keyed under the pre-ADR-0012 derivation; 'encryption migrate' fixes it.</summary>
        public const int LegacyKeyDerivation = 22;

        /// <summary>The Bitwarden CLI (bws) is not on PATH or cannot be started.</summary>
        public const int BwsNotInstalled = 23;

        /// <summary>bws did not answer within its bound; retryable.</summary>
        public const int BwsTimedOut = 24;

        /// <summary>bws ran but gave nothing usable: a non-zero exit (auth, unknown secret id) or empty output.</summary>
        public const int BwsFailed = 25;

        /// <summary>The Bitwarden secret is not a parseable OpenSSH ed25519 private key.</summary>
        public const int SecretNotAKey = 26;

        /// <summary>The memory.db.source sidecar is corrupt, names an unknown source, or is bitwarden without a secret id.</summary>
        public const int SourceSidecarInvalid = 27;

        /// <summary>encryption unset cannot rekey back to env: AIRACCOON_DB_PASSPHRASE is not set.</summary>
        public const int NoEnvPassphrase = 28;

        /// <summary>encryption bitwarden did not switch the source: the bank opens with the env passphrase.</summary>
        public const int BankKeyedToEnv = 29;
    }

    /// <summary>The bank file itself: absent, corrupt, locked, schema shape or version, repair not converging.</summary>
    public static class Bank
    {
        /// <summary>SQLite refused to open or read the bank; the general bank failure.</summary>
        public const int OpenFailed = 30;

        /// <summary>No bank file exists at the resolved path (wrong --data-root, or never served).</summary>
        public const int NoBank = 31;

        /// <summary>The file exists but is not a SQLite database (SQLITE_NOTADB); a wrong key looks the same.</summary>
        public const int Corrupted = 32;

        /// <summary>The bank is locked or busy (SQLITE_BUSY, SQLITE_LOCKED): another process holds it; retryable.</summary>
        public const int Busy = 33;

        /// <summary>The schema shape differs from this binary's DDL; serve repairs it on open.</summary>
        public const int SchemaMismatch = 34;

        /// <summary>The bank's user_version is newer than this binary supports; update ai-raccoon.</summary>
        public const int SchemaNewerThanBinary = 35;

        /// <summary>The schema is healthy but a model_migration row is open; MCP tool calls are refused until the re-embed finishes.</summary>
        public const int MigrationOpen = 36;

        /// <summary>repair project-ids --apply stopped with actionable rows still in place and nothing moving.</summary>
        public const int RepairStuck = 37;

        /// <summary>repair project-ids --apply hit its bound while census totals grew: writers are active; retryable.</summary>
        public const int RepairWritersActive = 38;

        /// <summary>repair project-ids --apply converged, but ids remain that only a person can attribute.</summary>
        public const int RepairAttentionNeeded = 39;
    }

    /// <summary>Something holds the port and this run cannot bind, cycle or use it.</summary>
    public static class Port
    {
        /// <summary>The port is in use by a holder this run cannot cycle or identify; the general port case.</summary>
        public const int InUse = 40;

        /// <summary>The listener on the port is not an ai-raccoon server.</summary>
        public const int ForeignListener = 41;

        /// <summary>serve --restart: another server took the port while this one was starting; retryable.</summary>
        public const int LostDuringRestart = 42;

        /// <summary>serve --restart: the port gave the probe no answer, so nothing was asked to stop, and the bind proved it held.</summary>
        public const int HeldUnanswered = 43;

        /// <summary>serve --restart: the server accepted the shutdown but still held the port at the bound.</summary>
        public const int RestartTimedOut = 44;
    }

    /// <summary>An ai-raccoon server answered but cannot serve this invocation.</summary>
    public static class Server
    {
        /// <summary>An ai-raccoon listener holds the port but did not prove it serves this data root.</summary>
        public const int Unproven = 50;

        /// <summary>This data root holds no token, so the server on the port cannot be asked anything.</summary>
        public const int NoToken = 51;

        /// <summary>serve --restart: the server refused this root's token; it serves another data root.</summary>
        public const int RestartTokenRefused = 52;

        /// <summary>A control-plane request got 401: the server refused this root's token; it may serve another data root.</summary>
        public const int RequestTokenRefused = 53;

        /// <summary>The proxy reached a proven backend, which would not open an MCP session with this root's token.</summary>
        public const int SessionRefused = 54;

        /// <summary>serve --restart: the server on the port is too old to accept a shutdown request.</summary>
        public const int TooOldToRestart = 55;

        /// <summary>serve observability: the server answered 404 on /observability; too old to report its PID.</summary>
        public const int TooOldForObservability = 56;

        /// <summary>A control-plane verb got 404 on its endpoint: the server predates the verb.</summary>
        public const int EndpointMissing = 57;

        /// <summary>serve observability otlp: the server does not export OTLP.</summary>
        public const int OtlpNotEnabled = 58;

        /// <summary>A model verb was refused (409) because a model_migration row is open; nothing changed; retryable.</summary>
        public const int MigrationRefused = 59;
    }

    /// <summary>No server could be reached, and none could be started.</summary>
    public static class Reach
    {
        /// <summary>No settings server answered at the port within the acquire budget, and none could be started there.</summary>
        public const int Unavailable = 60;

        /// <summary>serve observability: nothing is listening on the port.</summary>
        public const int NothingListening = 61;

        /// <summary>A settings server was acquired but a request failed at the transport; the write did not land.</summary>
        public const int StoppedAnswering = 62;

        /// <summary>The proxy found no backend on the port and could not start one there.</summary>
        public const int BackendUnavailable = 63;

        /// <summary>The listener did not prove its identity and the private fallback backend could not be started either.</summary>
        public const int PrivateFallbackFailed = 64;

        /// <summary>The backend executable could not be started as a process.</summary>
        public const int StartFailed = 65;

        /// <summary>This process cannot auto-start a backend: launched through the dotnet host, or its path is unknown.</summary>
        public const int AutoStartUnsupported = 66;
    }

    /// <summary>An embedding model or endpoint is unusable: hub download, local model directory, remote endpoint.</summary>
    public static class Model
    {
        /// <summary>A model download failed after the plan was accepted; nothing half-installed is left.</summary>
        public const int DownloadFailed = 70;

        /// <summary>Hugging Face did not resolve the repo id at the revision.</summary>
        public const int RepoNotFound = 71;

        /// <summary>The model hub could not be reached before any file was planned; retryable.</summary>
        public const int HubUnreachable = 72;

        /// <summary>A downloaded file's SHA-256 does not match its LFS pin.</summary>
        public const int ChecksumMismatch = 73;

        /// <summary>The downloaded ONNX graph failed the ONNX Runtime smoke load, or is not valid protobuf.</summary>
        public const int RuntimeRejected = 74;

        /// <summary>The repo cannot be planned: no ONNX export, unsupported model or tokenizer, missing metadata.</summary>
        public const int RepoUnsupported = 75;

        /// <summary>A local model directory is unusable: no or invalid manifest.json, or a declared file missing or re-hashed.</summary>
        public const int ManifestRejected = 76;

        /// <summary>model embedding set openai: the embedding endpoint could not be reached or refused the probe.</summary>
        public const int EndpointUnreachable = 77;

        /// <summary>model embedding set openai: base-url is not a usable absolute http(s) URL.</summary>
        public const int BadBaseUrl = 78;

        /// <summary>model embedding set openai: the endpoint's output dimension contradicts --dims, or is not 384 with no --dims.</summary>
        public const int DimensionMismatch = 79;
    }

    /// <summary>The local machine refuses: filesystem permissions, read-only root, disk space, state-directory secrets.</summary>
    public static class Environment
    {
        /// <summary>A local filesystem operation failed for a reason no narrower case names.</summary>
        public const int IoFailed = 80;

        /// <summary>The OS denied access under --data-root.</summary>
        public const int PermissionDenied = 81;

        /// <summary>--data-root is on a read-only filesystem.</summary>
        public const int ReadOnlyDataRoot = 82;

        /// <summary>--data-root is too long for this filesystem.</summary>
        public const int PathTooLong = 83;

        /// <summary>The target volume has less free space than the model download needs; nothing was touched.</summary>
        public const int DiskFull = 84;

        /// <summary>serve cannot read, heal or mint the MCP loopback token in the state directory.</summary>
        public const int TokenUnavailable = 85;

        /// <summary>serve cannot read or mint the identity key in the state directory.</summary>
        public const int IdentityKeyUnavailable = 86;

        /// <summary>The state directory or a secret file in it is not owner-only; serve refuses to use it.</summary>
        public const int SecretNotPrivate = 87;

        /// <summary>serve: the bind failed for a reason other than address-in-use, such as a denied port.</summary>
        public const int BindDenied = 88;
    }

    /// <summary>The product broke: a server 5xx, an unusable response, an unexpected exception, a bug guard.</summary>
    public static class Internal
    {
        /// <summary>The command failed for a reason no other code names; stderr carries the exception message.</summary>
        public const int Unexpected = 90;

        /// <summary>A control-plane request reached the server, which failed processing it (HTTP 5xx); retryable.</summary>
        public const int ServerError = 91;

        /// <summary>The server answered with a status the CLI cannot use (a 4xx other than 400, 401, 404 and 409).</summary>
        public const int UnusableResponse = 92;

        /// <summary>A model download succeeded but the manifest it generated failed validation: a product bug.</summary>
        public const int ManifestBug = 93;

        /// <summary>A parsed command path has no dispatch arm: the tree and the dispatcher disagree.</summary>
        public const int UnhandledCommand = 94;

        /// <summary>An operation timed out inside the command, outside the paths that name their own timeout; retryable.</summary>
        public const int Timeout = 95;
    }
}
