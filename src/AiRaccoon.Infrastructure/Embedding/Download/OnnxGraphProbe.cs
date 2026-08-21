namespace AiRaccoon.Infrastructure.Embedding.Download;

/// <summary>What the downloader needs to know about a model's ONNX graph before fetching its siblings.</summary>
public sealed record OnnxGraphProbe(
    IReadOnlyList<string> ExternalDataFiles,
    IReadOnlyList<string> InputNames,
    IReadOnlyList<string> OutputNames,
    int IrVersion,
    int? OpsetVersion);

/// <summary>The file is not a parseable ONNX protobuf (or the graph is malformed).</summary>
public sealed class OnnxProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Parses ONNX ModelProto bytes into the graph facts the downloader needs.</summary>
public interface IOnnxGraphProbeReader
{
    /// <summary>Throws <see cref="OnnxProbeException" /> when the bytes are not a valid ONNX protobuf.</summary>
    OnnxGraphProbe Read(byte[] onnxBytes);
}

/// <summary>
///     Reads the ONNX ModelProto with a minimal hand-rolled protobuf wire-format walker (no
///     protobuf dependency): ir_version, opset_import, graph inputs/outputs, and — the load-bearing
///     part — each initializer's external_data entries (D4 m6): the official bge-m3 export is a
///     stub model.onnx plus a 2.11 GiB model.onnx_data sibling that only the protobuf can name.
///     Unknown fields are skipped by wire type; malformed input fails with an actionable message.
///     Stateless and injectable; file I/O is the caller's job (the service reads the stub it
///     downloaded and passes the bytes).
/// </summary>
public sealed class OnnxGraphProbeReader : IOnnxGraphProbeReader
{
    private const int MaxDepth = 16;

    public OnnxGraphProbe Read(byte[] onnxBytes)
    {
        var builder = new ProbeBuilder();
        try
        {
            WalkModel(onnxBytes, builder, 0);
        }
        catch (OnnxProbeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
        {
            throw new OnnxProbeException("the file is not a valid ONNX protobuf (malformed field data)", ex);
        }

        return builder.Build();
    }

    private static void WalkModel(ReadOnlySpan<byte> message, ProbeBuilder builder, int depth)
    {
        GuardDepth(depth);
        var reader = new ProtoReader(message);
        while (reader.TryTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 0): // ir_version
                    builder.IrVersion = (int)reader.ReadVarint();
                    break;
                case (2, 2): // producer_name
                    reader.SkipBytes();
                    break;
                case (7, 2): // graph
                    WalkGraph(reader.ReadBytes(), builder, depth + 1);
                    break;
                case (8, 2): // opset_import
                    WalkOpset(reader.ReadBytes(), builder);
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }
    }

    private static void WalkGraph(ReadOnlySpan<byte> message, ProbeBuilder builder, int depth)
    {
        GuardDepth(depth);
        var reader = new ProtoReader(message);
        while (reader.TryTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 2): // name
                    reader.SkipBytes();
                    break;
                case (5, 2): // initializer
                    WalkTensor(reader.ReadBytes(), builder, depth + 1);
                    break;
                case (11, 2) or (12, 2): // input / output ValueInfoProto
                    builder.AddValueInfo(reader.ReadBytes(), field == 11);
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }
    }

    private static void WalkTensor(ReadOnlySpan<byte> message, ProbeBuilder builder, int depth)
    {
        GuardDepth(depth);
        var reader = new ProtoReader(message);
        var external = false;
        var location = string.Empty;
        while (reader.TryTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 0): // dims
                case (2, 0): // data_type
                    reader.ReadVarint();
                    break;
                case (3, 2): // name
                    reader.SkipBytes();
                    break;
                case (13, 2): // external_data StringStringEntryProto
                    var (key, value) = WalkStringStringEntry(reader.ReadBytes(), depth + 1);
                    if (key == "location")
                    {
                        location = value;
                    }

                    break;
                case (14, 0): // data_location (0 DEFAULT, 1 EXTERNAL)
                    external = reader.ReadVarint() == 1;
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        if (external && !string.IsNullOrEmpty(location))
        {
            builder.AddExternalData(location);
        }
    }

    private static (string Key, string Value) WalkStringStringEntry(ReadOnlySpan<byte> message, int depth)
    {
        GuardDepth(depth);
        var reader = new ProtoReader(message);
        var key = string.Empty;
        var value = string.Empty;
        while (reader.TryTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 2):
                    key = reader.ReadString();
                    break;
                case (2, 2):
                    value = reader.ReadString();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }

        return (key, value);
    }

    private static void WalkOpset(ReadOnlySpan<byte> message, ProbeBuilder builder)
    {
        var reader = new ProtoReader(message);
        var domain = string.Empty;
        while (reader.TryTag(out var field, out var wireType))
        {
            switch (field, wireType)
            {
                case (1, 2):
                    domain = reader.ReadString();
                    break;
                case (2, 0) when domain.Length == 0: // the default-domain opset is the one that matters
                    builder.OpsetVersion = (int)reader.ReadVarint();
                    break;
                case (2, 0):
                    reader.ReadVarint();
                    break;
                default:
                    reader.Skip(wireType);
                    break;
            }
        }
    }

    /// <summary>
    ///     Forward-only wire-format reader over a message span. Every read that overruns the
    ///     message throws <see cref="OnnxProbeException" /> with an actionable message.
    /// </summary>
    private static void GuardDepth(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new OnnxProbeException("the file is not a valid ONNX protobuf (message nesting exceeds the depth limit)");
        }
    }

    private ref struct ProtoReader(ReadOnlySpan<byte> message)
    {
        private readonly ReadOnlySpan<byte> _message = message;
        private int _position;

        public bool TryTag(out int field, out int wireType)
        {
            if (_position >= _message.Length)
            {
                field = 0;
                wireType = 0;
                return false;
            }

            var tag = ReadVarint();
            field = (int)(tag >> 3);
            wireType = (int)(tag & 0x7);
            if (field == 0)
            {
                throw new OnnxProbeException("the file is not a valid ONNX protobuf (field number 0 is illegal)");
            }

            return true;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = ReadByte();
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new OnnxProbeException("the file is not a valid ONNX protobuf (varint overflows)");
        }

        public ReadOnlySpan<byte> ReadBytes()
        {
            var length = ReadVarint();
            if (length > int.MaxValue || (ulong)(_message.Length - _position) < length)
            {
                throw new OnnxProbeException("the file is not a valid ONNX protobuf (field length overruns the message)");
            }

            var bytes = _message.Slice(_position, (int)length);
            _position += (int)length;
            return bytes;
        }

        public string ReadString() => System.Text.Encoding.UTF8.GetString(ReadBytes());

        public void SkipBytes() => _ = ReadBytes();

        public void Skip(int wireType)
        {
            switch (wireType)
            {
                case 0:
                    _ = ReadVarint();
                    break;
                case 1:
                    _position += 8;
                    break;
                case 2:
                    SkipBytes();
                    break;
                case 5:
                    _position += 4;
                    break;
                default:
                    throw new OnnxProbeException($"the file is not a valid ONNX protobuf (unsupported wire type {wireType})");
            }

            if (_position > _message.Length)
            {
                throw new OnnxProbeException("the file is not a valid ONNX protobuf (field overruns the message)");
            }
        }

        private byte ReadByte()
        {
            if (_position >= _message.Length)
            {
                throw new OnnxProbeException("the file is not a valid ONNX protobuf (truncated field data)");
            }

            return _message[_position++];
        }
    }

    private sealed class ProbeBuilder
    {
        private readonly List<string> _externalData = [];
        private readonly List<string> _inputs = [];
        private readonly List<string> _outputs = [];

        public int IrVersion { get; set; }

        public int? OpsetVersion { get; set; }

        public void AddExternalData(string location)
        {
            if (!_externalData.Contains(location, StringComparer.Ordinal))
            {
                _externalData.Add(location);
            }
        }

        public void AddValueInfo(ReadOnlySpan<byte> message, bool isInput)
        {
            var reader = new ProtoReader(message);
            var name = string.Empty;
            while (reader.TryTag(out var field, out var wireType))
            {
                if (field == 1 && wireType == 2)
                {
                    name = reader.ReadString();
                }
                else
                {
                    reader.Skip(wireType);
                }
            }

            if (name.Length > 0)
            {
                (isInput ? _inputs : _outputs).Add(name);
            }
        }

        public OnnxGraphProbe Build() => new(_externalData, _inputs, _outputs, IrVersion, OpsetVersion);
    }
}
