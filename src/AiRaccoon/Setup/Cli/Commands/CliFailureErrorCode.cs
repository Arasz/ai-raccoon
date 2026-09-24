using System.Net;
using System.Net.Sockets;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>The <see cref="ErrorCode" /> for a command that threw: the case the exception names, and
/// <see cref="ErrorCode.Internal.Unexpected" /> when none does — never "you mistyped" by default.</summary>
internal static class CliFailureErrorCode
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteNotADatabase = 26;

    internal static int For(Exception ex) =>
        ex switch
        {
            UndialablePortException => ErrorCode.Usage.UndialablePort,
            ArgumentException or FormatException => ErrorCode.Usage.InvalidValue,
            HttpRequestException { StatusCode: HttpStatusCode.BadRequest } => ErrorCode.Usage.RequestRejected,
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => ErrorCode.Server.EndpointMissing,
            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => ErrorCode.Internal.ServerError,
            HttpRequestException { StatusCode: not null } => ErrorCode.Internal.UnusableResponse,
            BankKeyMismatchException { LegacyDerivation: true } => ErrorCode.Key.LegacyKeyDerivation,
            BankKeyMismatchException => ErrorCode.Key.WrongKey,
            BankCorruptedException => ErrorCode.Bank.Corrupted,
            SqliteException sqlite => sqlite.SqliteErrorCode switch
            {
                SqliteBusy or SqliteLocked => ErrorCode.Bank.Busy,
                SqliteNotADatabase => ErrorCode.Bank.Corrupted,
                _ => ErrorCode.Bank.OpenFailed
            },
            BwsInvocationException bws => bws.Failure switch
            {
                BwsFailure.NotInstalled => ErrorCode.Key.BwsNotInstalled,
                BwsFailure.TimedOut => ErrorCode.Key.BwsTimedOut,
                _ => ErrorCode.Key.BwsFailed
            },
            EncryptionKeyException => ErrorCode.Key.SecretNotAKey,
            EncryptionSourceException => ErrorCode.Key.SourceSidecarInvalid,
            ModelMigrationInProgressException => ErrorCode.Server.MigrationRefused,
            EmbeddingEndpointUnreachableException => ErrorCode.Model.EndpointUnreachable,
            EmbeddingDimensionMismatchException => ErrorCode.Model.DimensionMismatch,
            CodeEngineActivationRefusedException or EmbeddingModelRejectedException => ErrorCode.Model.ManifestRejected,
            UnhandledCommandException => ErrorCode.Internal.UnhandledCommand,
            UnauthorizedAccessException => ErrorCode.Environment.PermissionDenied,
            IOException io when CliFailureFormatting.IsReadOnly(io) => ErrorCode.Environment.ReadOnlyDataRoot,
            IOException io when CliFailureFormatting.IsTooLong(io) => ErrorCode.Environment.PathTooLong,
            _ when IsBindDenied(ex) => ErrorCode.Environment.BindDenied,
            IOException => ErrorCode.Environment.IoFailed,
            OperationCanceledException => ErrorCode.Internal.Timeout,
            _ => ErrorCode.Internal.Unexpected
        };

    /// <summary>The first code that names the failure, or <paramref name="general" /> when only the catch-all would.</summary>
    internal static int For(Exception? ex, int general) =>
        ex is null || For(ex) == ErrorCode.Internal.Unexpected ? general : For(ex);

    private static bool IsBindDenied(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException { SocketErrorCode: SocketError.AccessDenied })
            {
                return true;
            }
        }

        return false;
    }
}
