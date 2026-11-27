using System.Reflection;
using AgentGate.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentGate.Tests;

public sealed class ToolRiskTests
{
    [Fact]
    public async Task EveryDeclaredAndRegisteredToolHasAnExplicitRiskLevel()
    {
        await using var factory = new McpTestApplication(null);
        var methods = typeof(Program).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();
        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            Assert.Single(method.GetCustomAttributes<AgentGate.Audit.ToolAuditParametersAttribute>());
            var risk = Assert.Single(method.GetCustomAttributes<ToolRiskAttribute>());
            Assert.True(Enum.IsDefined(risk.Level), "Érvénytelen eszközkockázati szint.");
        }

        var options = factory.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var tools = options.ToolCollection;
        Assert.NotNull(tools);
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            Assert.Single(tool.Metadata.OfType<AgentGate.Audit.ToolAuditParametersAttribute>());
            var risk = Assert.Single(tool.Metadata.OfType<ToolRiskAttribute>());
            Assert.True(Enum.IsDefined(risk.Level), "Érvénytelen regisztrált eszközkockázati szint.");
        }
        Assert.Equal(methods.Length, tools.Count);
        var ping = Assert.Single(tools, tool => tool.ProtocolTool.Name == "gate_ping");
        Assert.Equal("gate_ping", ping.ProtocolTool.Name);
        Assert.Empty(Assert.Single(ping.Metadata.OfType<AgentGate.Audit.ToolAuditParametersAttribute>()).Names);
        Assert.Equal(RiskLevel.Read, Assert.Single(ping.Metadata.OfType<ToolRiskAttribute>()).Level);
        var status = Assert.Single(tools, tool => tool.ProtocolTool.Name == "gate_approval_status");
        Assert.Equal(RiskLevel.Read, Assert.Single(status.Metadata.OfType<ToolRiskAttribute>()).Level);
        Assert.Equal(["requestId", "wait_seconds"], Assert.Single(status.Metadata.OfType<AgentGate.Audit.ToolAuditParametersAttribute>()).Names);
        var approvalTest = Assert.Single(tools, tool => tool.ProtocolTool.Name == "gate_approval_test");
        Assert.Equal(RiskLevel.SensitiveControl, Assert.Single(approvalTest.Metadata.OfType<ToolRiskAttribute>()).Level);
        Assert.Equal(["text"], Assert.Single(approvalTest.Metadata.OfType<AgentGate.Audit.ToolAuditParametersAttribute>()).Names);
    }

    [Fact]
    public void RiskLevelsMatchTheSixRequiredCategories()
    {
        Assert.Equal(
            ["Read", "Control", "SensitiveControl", "Configuration", "System", "Forbidden"],
            Enum.GetNames<RiskLevel>());
    }
}
