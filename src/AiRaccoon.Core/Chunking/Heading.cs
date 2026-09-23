namespace AiRaccoon.Core.Chunking;

/// <summary>One heading on <see cref="HeadingStack" />'s stack: its ATX level and its (possibly identifier-trimmed) text.</summary>
internal readonly record struct Heading(int Level, string Text);
