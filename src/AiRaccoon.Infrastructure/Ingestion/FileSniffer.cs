using System.Text;
using AiRaccoon.Core.Ingestion;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     Reads only the leading <see cref="BinaryContent.InspectedLength" /> characters of a file to
///     decide whether it is binary — the same prefix <see cref="BinaryContent.IsBinary" /> inspects.
/// </summary>
public sealed class FileSniffer
{
    public async Task<bool> IsBinaryAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await IsBinaryAsync(stream, cancellationToken);
    }

    public async Task<bool> IsBinaryAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        var buffer = new char[BinaryContent.InspectedLength];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        return BinaryContent.IsBinary(new string(buffer, 0, count));
    }
}
