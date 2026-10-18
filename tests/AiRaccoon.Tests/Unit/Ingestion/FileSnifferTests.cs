using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Ingestion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

/// <summary>The sniffer answers from a bounded prefix and agrees with <see cref="BinaryContent" />.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FileSnifferTests
{
    private readonly FileSniffer _sniffer = new();

    [Fact]
    public async Task IsBinaryAsync_NulInThePrefix_IsBinary()
    {
        using var stream = new MemoryStream([(byte)'a', 0, (byte)'b']);

        (await _sniffer.IsBinaryAsync(stream, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task IsBinaryAsync_NulPastThePrefix_IsNotBinary()
    {
        using var stream = new MemoryStream(
            [.. Enumerable.Repeat((byte)'a', BinaryContent.InspectedLength), 0, (byte)'b']);

        (await _sniffer.IsBinaryAsync(stream, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task IsBinaryAsync_LargeStream_ReadsOnlyABoundedPrefix()
    {
        using var stream = new CountingStream(64L * 1024 * 1024);

        await _sniffer.IsBinaryAsync(stream, TestContext.Current.CancellationToken);

        stream.BytesRead.ShouldBeLessThanOrEqualTo(64 * 1024);
    }

    [Fact]
    public async Task IsBinaryAsync_Path_ReadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sniff-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [0, 1, 2], TestContext.Current.CancellationToken);
        try
        {
            (await _sniffer.IsBinaryAsync(path, TestContext.Current.CancellationToken)).ShouldBeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A zero-filled read-only stream of a fixed length that counts the bytes served.</summary>
    private sealed class CountingStream(long length) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var served = (int)Math.Min(buffer.Length, length - BytesRead);
            buffer[..served].Fill((byte)'a');
            BytesRead += served;
            return served;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
