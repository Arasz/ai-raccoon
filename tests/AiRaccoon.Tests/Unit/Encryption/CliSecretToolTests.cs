using System.Reflection;
using AiRaccoon.Infrastructure.Encryption;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Encryption;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliSecretToolTests
{
    /// <summary>The runner strips every tool's token but its own from a child, so a tool left out of All would leak its token to the others.</summary>
    [Fact]
    public void All_ListsEveryDeclaredTool()
    {
        var declared = typeof(CliSecretTool)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(CliSecretTool))
            .Select(field => (CliSecretTool)field.GetValue(null)!)
            .ToArray();

        declared.ShouldNotBeEmpty();
        CliSecretTool.All.ShouldBe(declared, ignoreOrder: true);
    }

    [Fact]
    public void All_TokenEnvVarsAreDistinct()
    {
        CliSecretTool.All.Select(tool => tool.TokenEnvVar).ShouldBeUnique();
    }
}
