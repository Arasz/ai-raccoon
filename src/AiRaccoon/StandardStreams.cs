namespace AiRaccoon;

public sealed record StandardStreams(TextReader Input, TextWriter Output, TextWriter Error)
{
    public void WriteErrorLine(string line) => Error.WriteLine(line);
    public Task WriteErrorLineAsync(string line) => Error.WriteLineAsync(line);

    public void WriteOutputLine(string line) => Output.WriteLine(line);
    public Task WriteOutputLineAsync(string line) => Output.WriteLineAsync(line);

    public Task WriteErrorAsync(string text) => Error.WriteAsync(text);

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => Input.ReadLineAsync(cancellationToken);
}
