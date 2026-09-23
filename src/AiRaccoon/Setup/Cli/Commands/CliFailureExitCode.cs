using System.Net;
using AiRaccoon.Core.Embedding;
using AiRaccoon.Core.Encryption;
using AiRaccoon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Setup.Cli.Commands;

/// <summary>The exit code for a command that threw: the code whose meaning fits the failure, and
/// <see cref="ExitCode.CommandFailed" /> when none does — never "you mistyped" by default.</summary>
internal static class CliFailureExitCode
{
    internal static int For(Exception ex) =>
        ex switch
        {
            ArgumentException or FormatException => ExitCode.InvalidArgument,
            CodeEngineActivationRefusedException or EmbeddingModelRejectedException => ExitCode.InvalidArgument,
            HttpRequestException { StatusCode: HttpStatusCode.BadRequest } => ExitCode.InvalidArgument,
            _ when CliFailureFormatting.BlamesDataRoot(ex) => ExitCode.InvalidArgument,
            SqliteException or BankKeyMismatchException => ExitCode.FailedToOpenEncryptedBank,
            ModelMigrationInProgressException => ExitCode.ModelResetRefused,
            _ => ExitCode.CommandFailed
        };
}
