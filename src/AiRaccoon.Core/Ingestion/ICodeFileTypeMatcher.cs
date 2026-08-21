namespace AiRaccoon.Core.Ingestion;

/// <summary>Membership test over the code extension registry (<see cref="CodeExtensions" />).</summary>
public interface ICodeFileTypeMatcher
{
    bool IsCodeFile(string path);
}
