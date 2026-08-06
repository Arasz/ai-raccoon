using AiRaccoon.Core.Encryption;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

public interface IEncryptionSourceSidecar
{
    string FilePath { get; }
    EncryptionData Read();
    void Write(EncryptionData config);
    void Delete();
}
