using System.Net;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>The exit code for a command that threw: the code whose meaning fits the failure, and
/// <see cref="ErrorCode.Internal.Unexpected" /> when none does — never "you mistyped" by default.</summary>
internal static class CliFailureExitCode
{
    internal static int For(Exception ex) =>
        ex switch
        {
            ArgumentException or FormatException => ErrorCode.Usage.InvalidValue,
            CodeEngineActivationRefusedException or EmbeddingModelRejectedException => ErrorCode.Usage.InvalidValue,
            HttpRequestException { StatusCode: HttpStatusCode.BadRequest } => ErrorCode.Usage.InvalidValue,
            _ when CliFailureFormatting.BlamesDataRoot(ex) => ErrorCode.Usage.InvalidValue,
            SqliteException or BankKeyMismatchException => ErrorCode.Bank.OpenFailed,
            ModelMigrationInProgressException => ErrorCode.Server.MigrationRefused,
            _ => ErrorCode.Internal.Unexpected
        };
}
