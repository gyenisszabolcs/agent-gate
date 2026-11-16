using AgentGate.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentGate.Tests;

public sealed class KillSwitchTests
{
    [Theory]
    [InlineData(RiskLevel.Read, true)]
    [InlineData(RiskLevel.Control, false)]
    [InlineData(RiskLevel.SensitiveControl, false)]
    [InlineData(RiskLevel.Configuration, false)]
    [InlineData(RiskLevel.System, false)]
    [InlineData(RiskLevel.Forbidden, false)]
    public void EnabledSwitchAllowsOnlyRead(RiskLevel level, bool expected)
    {
        var monitor = new TestOptionsMonitor { Value = new KillSwitchOptions { Enabled = true } };
        var policy = new KillSwitchPolicy(monitor);

        Assert.Equal(expected, policy.IsAllowed(level));
    }

    [Theory]
    [InlineData(RiskLevel.Read, true)]
    [InlineData(RiskLevel.Control, true)]
    [InlineData(RiskLevel.SensitiveControl, true)]
    [InlineData(RiskLevel.Configuration, true)]
    [InlineData(RiskLevel.System, true)]
    [InlineData(RiskLevel.Forbidden, false)]
    [InlineData((RiskLevel)(-1), false)]
    [InlineData((RiskLevel)6, false)]
    [InlineData((RiskLevel)int.MaxValue, false)]
    public void DisabledSwitchAllowsOnlyKnownNonForbiddenLevels(RiskLevel level, bool expected)
    {
        var monitor = new TestOptionsMonitor { Value = new KillSwitchOptions { Enabled = false } };
        var policy = new KillSwitchPolicy(monitor);

        Assert.Equal(expected, policy.IsAllowed(level));
    }

    public static TheoryData<RiskLevel, bool> ClosedCases => new()
    {
        { RiskLevel.Read, true },
        { RiskLevel.Control, false },
        { RiskLevel.Forbidden, false }
    };

    [Theory]
    [MemberData(nameof(ClosedCases))]
    public void MissingSettingAllowsOnlyRead(RiskLevel level, bool expected)
    {
        var policy = new KillSwitchPolicy(new TestOptionsMonitor());

        Assert.Equal(expected, policy.IsAllowed(level));
    }

    [Theory]
    [MemberData(nameof(ClosedCases))]
    public void UnavailableSourceAllowsOnlyRead(RiskLevel level, bool expected)
    {
        var monitor = new TestOptionsMonitor { Error = new IOException("A tesztforrás nem érhető el.") };
        var policy = new KillSwitchPolicy(monitor);

        Assert.Equal(expected, policy.IsAllowed(level));
    }

    [Fact]
    public void EveryCheckReadsCurrentStateWithoutKeepingOpenState()
    {
        var monitor = new TestOptionsMonitor { Value = new KillSwitchOptions { Enabled = false } };
        var policy = new KillSwitchPolicy(monitor);
        Assert.True(policy.IsAllowed(RiskLevel.Control));

        monitor.Value = new KillSwitchOptions { Enabled = true };
        Assert.False(policy.IsAllowed(RiskLevel.Control));

        monitor.Value = new KillSwitchOptions { Enabled = false };
        Assert.True(policy.IsAllowed(RiskLevel.Control));

        monitor.Error = new OptionsValidationException(Options.DefaultName, typeof(KillSwitchOptions), ["Hibás tesztbeállítás."]);
        Assert.False(policy.IsAllowed(RiskLevel.Control));
        Assert.True(policy.IsAllowed(RiskLevel.Read));

        monitor.Error = null;
        monitor.Value = new KillSwitchOptions();
        Assert.False(policy.IsAllowed(RiskLevel.Control));

        monitor.Value = new KillSwitchOptions { Enabled = false };
        Assert.True(policy.IsAllowed(RiskLevel.Control));
        Assert.Equal(7, monitor.ReadCount);
    }

    [Theory]
    [MemberData(nameof(ClosedCases))]
    public void MalformedConfigurationAllowsOnlyRead(RiskLevel level, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KillSwitch:Enabled"] = "nem-logikai-ertek"
        }).Build();
        using var configurationLifetime = (IDisposable)configuration;
        var services = new ServiceCollection();
        services.AddOptions<KillSwitchOptions>().Bind(configuration.GetSection("KillSwitch"));
        using var provider = services.BuildServiceProvider();
        var policy = new KillSwitchPolicy(provider.GetRequiredService<IOptionsMonitor<KillSwitchOptions>>());

        Assert.Equal(expected, policy.IsAllowed(level));
    }

    [Theory]
    [MemberData(nameof(ClosedCases))]
    public void MissingConfigurationSectionAllowsOnlyRead(RiskLevel level, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        using var configurationLifetime = (IDisposable)configuration;
        var services = new ServiceCollection();
        services.AddOptions<KillSwitchOptions>().Bind(configuration.GetSection("KillSwitch"));
        using var provider = services.BuildServiceProvider();
        var policy = new KillSwitchPolicy(provider.GetRequiredService<IOptionsMonitor<KillSwitchOptions>>());

        Assert.Equal(expected, policy.IsAllowed(level));
    }

    [Fact]
    public void RealOptionsMonitorReloadsWithoutRestartAndRecoversAfterInvalidConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KillSwitch:Enabled"] = "false"
        }).Build();
        using var configurationLifetime = (IDisposable)configuration;
        var services = new ServiceCollection();
        services.AddOptions<KillSwitchOptions>().Bind(configuration.GetSection("KillSwitch"));
        using var provider = services.BuildServiceProvider();
        var policy = new KillSwitchPolicy(provider.GetRequiredService<IOptionsMonitor<KillSwitchOptions>>());
        Assert.True(policy.IsAllowed(RiskLevel.System));

        configuration["KillSwitch:Enabled"] = "true";
        configuration.Reload();
        Assert.False(policy.IsAllowed(RiskLevel.System));

        configuration["KillSwitch:Enabled"] = "false";
        configuration.Reload();
        Assert.True(policy.IsAllowed(RiskLevel.System));

        configuration["KillSwitch:Enabled"] = "hibas";
        // A monitor már az újratöltés értesítésekor jelzi a kötési hibát.
        Assert.NotNull(Record.Exception(configuration.Reload));
        Assert.False(policy.IsAllowed(RiskLevel.System));
        Assert.True(policy.IsAllowed(RiskLevel.Read));

        configuration["KillSwitch:Enabled"] = null;
        configuration.Reload();
        Assert.False(policy.IsAllowed(RiskLevel.System));

        configuration["KillSwitch:Enabled"] = "false";
        configuration.Reload();
        Assert.True(policy.IsAllowed(RiskLevel.System));
    }

    private sealed class TestOptionsMonitor : IOptionsMonitor<KillSwitchOptions>
    {
        public KillSwitchOptions Value { get; set; } = new();
        public Exception? Error { get; set; }
        public int ReadCount { get; private set; }
        public KillSwitchOptions CurrentValue
        {
            get
            {
                ReadCount++;
                if (Error is not null)
                {
                    throw Error;
                }

                return Value;
            }
        }
        public KillSwitchOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<KillSwitchOptions, string?> listener) => null;
    }
}
