using AiRaccoon.Core.Encryption;

namespace AiRaccoon.Infrastructure.Sqlite.Encryption;

public interface IEncryptionState
{
    EncryptionData Read();
    void Write(EncryptionData config);
    void Delete();
}
