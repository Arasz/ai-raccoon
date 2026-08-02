# High-performance logging

Use a nested static partial `Log` class with static `[LoggerMessage]`-attributed methods (taking `ILogger` as a parameter, with an explicit `EventId`) instead of calling `logger.LogInformation(...)`/`LogError(...)` etc. directly — it avoids boxing/allocation on the hot path and keeps event ids centrally discoverable.
