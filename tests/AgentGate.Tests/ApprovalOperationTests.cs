using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Policy;
using Xunit;

namespace AgentGate.Tests;

public sealed class ApprovalOperationTests
{
    [Fact]
    public void HashCoversToolRiskAndEveryEffectiveParameterAndFreezesSource()
    {
        var source = new Dictionary<string, string?> { ["visible"] = "a", ["hidden"] = "b", ["default"] = "1" };
        var prepared = new PreparedOperation("probe", RiskLevel.System, source);
        var originalHash = prepared.ComputeHash();
        source["hidden"] = "changed";
        Assert.Equal(originalHash, prepared.ComputeHash());
        var reordered = new PreparedOperation("probe", RiskLevel.System, new Dictionary<string, string?>
        { ["default"] = "1", ["hidden"] = "b", ["visible"] = "a" });
        Assert.Equal(originalHash, reordered.ComputeHash());
        Assert.NotEqual(originalHash, new PreparedOperation("other", RiskLevel.System, reordered.Parameters).ComputeHash());
        Assert.NotEqual(originalHash, new PreparedOperation("probe", RiskLevel.Configuration, reordered.Parameters).ComputeHash());
        Assert.NotEqual(originalHash, new PreparedOperation("probe", RiskLevel.System, source).ComputeHash());
        source["hidden"] = "b";
        source["default"] = "2";
        Assert.NotEqual(originalHash, new PreparedOperation("probe", RiskLevel.System, source).ComputeHash());
    }

    [Fact]
    public void HashBindsServerOwnerContextAsWellAsOperation()
    {
        var operation = new PreparedOperation("probe", RiskLevel.System, new Dictionary<string, string?>());
        Assert.NotEqual(operation.ComputeHash("Mikaela"), operation.ComputeHash("other"));
    }

    [Fact]
    public void EquivalentValidatedUnicodeAndTrimmedInputShareHash()
    {
        var handler = new ApprovalTestOperation();
        using var left = JsonDocument.Parse("{\"text\":\" próba \"}");
        using var right = JsonDocument.Parse("{\"text\":\"próba\"}");
        var first = handler.Prepare(left.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
        var second = handler.Prepare(right.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value));
        Assert.Equal(first.ComputeHash(), second.ComputeHash());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":42}")]
    [InlineData("{\"text\":\" \"}")]
    [InlineData("{\"text\":\"x\",\"other\":true}")]
    [InlineData("{\"text\":\"x\\u0001y\"}")]
    public void TestOperationRejectsInvalidInput(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => new ApprovalTestOperation().Prepare(
            document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)));
    }
}
