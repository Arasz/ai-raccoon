# Fixing Property Allocation Leaks in C# Records

When a C# `record` or `record struct` uses a computed property (arrow function `=>`) that executes `.ToList()`, `.ToArray()`, or `.ToHashSet()`, it allocates a new collection on every read. In tight loops or during serialization, this
creates a silent performance trap that violates the immutable wrapper principle.

## Fix for `record struct`

Remove the primary constructor if it forces all properties to be evaluated externally. Define the properties manually and create an explicit constructor that computes the derived collection exactly once:

```csharp
private readonly record struct UploadedFiles
{
    public IReadOnlyList<RawFileBytes> Files { get; }
    public IReadOnlyCollection<RawFileBytes> UnsupportedFiles { get; }

    public UploadedFiles(IReadOnlyList<RawFileBytes> files)
    {
        Files = files;
        // Evaluated exactly once on instantiation
        UnsupportedFiles = files.Where(f => f.FileType == FileType.Other).ToList();
    }
}
```

## Fix for `record` with `init` properties

Use an explicit `init` block with a backing field to compute the dependent collection safely during initialization:

```csharp
public sealed record AtsComplianceReport
{
    private readonly IReadOnlyList<AtsRuleCheckResult> _results = [];
    
    public required IReadOnlyList<AtsRuleCheckResult> Results
    {
        get => _results;
        init
        {
            _results = value;
            // Evaluated exactly once when Results is initialized
            FailedRuleIds = value.Where(r => !r.Passed).Select(r => r.RuleId).ToList();
        }
    }
    
    public IReadOnlyList<string> FailedRuleIds { get; private set; } = [];
}
```
