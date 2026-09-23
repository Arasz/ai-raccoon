namespace AiRaccoon.Hosting.Common;

/// <summary>--port is 0 or outside 1-65535 on a path that has to dial a fixed port.</summary>
internal sealed class UndialablePortException(string message) : Exception(message);
