using System.CommandLine;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;

namespace AiRaccoon.Setup.Cli.Options;

public static class CliOptionReader
{
    extension(ParseResult parseResult)
    {
        public OptionReadResult<TCliOptions> ReadOptions<TCliOptions>(Func<ParseResult, List<string>, TCliOptions> reader)
        {
            var collectedErrors = new List<string>();
            var options = reader(parseResult, collectedErrors);
            return collectedErrors.Count > 0 ? OptionReadResult<TCliOptions>.Success(options) : OptionReadResult<TCliOptions>.Failure(collectedErrors);
        }

        /// <summary>
        ///     Reads one option's value; a missing option (or one requiring tokens that has
        ///     none) returns <paramref name="defaultValue" />, and a coercion failure records its message
        ///     in <paramref name="errors" /> and also returns <paramref name="defaultValue" />.
        /// </summary>
        public T ReadOption<T>(string optionName, T defaultValue, List<string> errors, bool requireTokens = true)
        {
            if (parseResult.GetResult(optionName) is not OptionResult result || requireTokens && result.Tokens.Count == 0)
            {
                return defaultValue;
            }

            try
            {
                return result.GetValueOrDefault<T>();
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
                return defaultValue;
            }
        }

        public T ReadOption<T>(Option<T> option, T defaultValue, List<string> errors, bool requireTokens = true)
        {
            if (parseResult.GetResult(option) is not { } result || requireTokens && result.Tokens.Count == 0)
            {
                return defaultValue;
            }

            try
            {
                return result.GetValueOrDefault<T>();
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(ex.Message);
                return defaultValue;
            }
        }
    }
}

public sealed record OptionReadResult<TOption>
{
    public TOption? Options { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    [MemberNotNullWhen(true, nameof(Options))]
    public required bool IsSuccess { get; init; }

    public static OptionReadResult<TOption> Success(TOption options) =>
        new()
        {
            IsSuccess = true,
            Options = options
        };

    public static OptionReadResult<TOption> Failure(List<string> errors) =>
        new()
        {
            Errors = errors,
            IsSuccess = false
        };
}
