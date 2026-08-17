namespace AiRaccoon.Core.Memory;

/// <summary>Live bank disk stats (`settings maintenance list`, ADR-0075 amendment): db/WAL/shm sizes plus what a checkpoint/vacuum could reclaim.</summary>
public sealed record BankStats(
    long DbBytes,
    long WalBytes,
    long ShmBytes,
    long FreelistBytes,
    long UncheckpointedWalBytes)
{
    public long TotalBytes => DbBytes + WalBytes + ShmBytes;

    public long ReclaimableBytes => FreelistBytes + UncheckpointedWalBytes;
}
