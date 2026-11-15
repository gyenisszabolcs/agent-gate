using System.Security.Cryptography;
using AgentGate.Secrets;
using Xunit;

namespace AgentGate.Tests;

public sealed class BitwardenSecretProviderTests
{
    [Fact]
    public async Task GetSecretAsync_ReturnsValueAndForwardsRequest()
    {
        var accessToken = NewSecret();
        var value = NewSecret();
        var secretId = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var client = new FakeClient((id, token, ct) =>
        {
            Assert.True(id == secretId, "Eltérő titokazonosító.");
            Assert.True(token == accessToken, "Eltérő gépi token.");
            Assert.True(ct == cancellation.Token, "Eltérő megszakítási token.");
            return Task.FromResult<string?>(value);
        });
        ISecretProvider provider = new BitwardenSecretProvider(client, name =>
        {
            if (name == "CREDENTIALS_DIRECTORY")
            {
                return null;
            }
            Assert.Equal("AGENT_GATE_BITWARDEN_ACCESS_TOKEN", name);
            return accessToken;
        });

        var result = await provider.GetSecretAsync(secretId, cancellation.Token);

        Assert.True(result == value, "A lekért titok eltér a várt értéktől.");
        Assert.Equal(1, client.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public async Task GetSecretAsync_RejectsMissingTokenWithoutCallingProvider(string? accessToken)
    {
        var client = new FakeClient((_, _, _) => throw new InvalidOperationException());
        var provider = new BitwardenSecretProvider(client, name => name == "CREDENTIALS_DIRECTORY" ? null : accessToken);

        var error = await Assert.ThrowsAsync<SecretProviderException>(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.Equal(SecretProviderError.MissingAccessToken, error.Error);
        Assert.True(error.Message == "A Bitwarden gépi token nincs beállítva.", "Eltérő hiányzótoken-hibaüzenet.");
        Assert.True(error.InnerException is null, "A biztonságos hiba belső kivételt tartalmaz.");
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task GetSecretAsync_ReportsMissingSecretWithoutSensitiveDetails()
    {
        var accessToken = NewSecret();
        var client = new FakeClient((_, _, _) => Task.FromResult<string?>(null));
        var provider = new BitwardenSecretProvider(client, name => name == "CREDENTIALS_DIRECTORY" ? null : accessToken);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is SecretProviderException, "A hiányzó titok nem biztonságos hibával zárult.");
        Assert.True(error!.Message == "A kért Bitwarden titok nem található vagy nem hozzáférhető.", "Eltérő hiányzótitok-hibaüzenet.");
        AssertSafe(error, accessToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetSecretAsync_SanitizesProviderFailures(bool authenticationFailure)
    {
        var accessToken = NewSecret();
        var value = NewSecret();
        var sensitiveMessage = accessToken + value;
        Exception providerError = authenticationFailure
            ? new UnauthorizedAccessException(sensitiveMessage, new Exception(value))
            : new InvalidOperationException(sensitiveMessage, new Exception(value));
        var client = new FakeClient((_, _, _) => Task.FromException<string?>(providerError));
        var provider = new BitwardenSecretProvider(client, name => name == "CREDENTIALS_DIRECTORY" ? null : accessToken);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is SecretProviderException, "A szolgáltatói kivétel nem lett biztonságos hibára cserélve.");
        AssertSafe(error!, accessToken, value);
        var expectedMessage = authenticationFailure ? "A Bitwarden hitelesítés sikertelen." : "A Bitwarden titok lekérése sikertelen.";
        Assert.True(error!.Message == expectedMessage, "Eltérő szolgáltatói hibaüzenet.");
    }

    [Fact]
    public async Task GetSecretAsync_PreCanceledRequestDoesNotReadTokenOrCallProvider()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new FakeClient((_, _, _) => throw new InvalidOperationException());
        var environmentRead = false;
        var provider = new BitwardenSecretProvider(client, _ =>
        {
            environmentRead = true;
            return NewSecret();
        });

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid(), cancellation.Token));

        Assert.True(error is OperationCanceledException, "A megszakított kérés nem megszakítási hibával zárult.");
        Assert.True(!environmentRead, "Megszakítás után környezeti változó olvasása történt.");
        Assert.Equal(0, client.Calls);
        Assert.True(((OperationCanceledException)error!).CancellationToken == cancellation.Token, "Eltérő megszakítási token.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task GetSecretAsync_CancellationDoesNotExposeProviderDetails(int completionMode)
    {
        var accessToken = NewSecret();
        var value = NewSecret();
        using var cancellation = new CancellationTokenSource();
        var client = new FakeClient(async (_, _, ct) =>
        {
            await Task.Yield();
            cancellation.Cancel();
            return completionMode switch
            {
                0 => throw new OperationCanceledException(accessToken + value, new Exception(value), ct),
                1 => value,
                _ => throw new InvalidOperationException(accessToken + value, new Exception(value))
            };
        });
        var provider = new BitwardenSecretProvider(client, name => name == "CREDENTIALS_DIRECTORY" ? null : accessToken);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid(), cancellation.Token));

        Assert.True(error is OperationCanceledException, "A megszakított kérés nem megszakítási hibával zárult.");
        AssertSafe(error!, accessToken, value);
        Assert.True(error!.Message == "A Bitwarden titok lekérése megszakítva.", "Eltérő megszakítási hibaüzenet.");
        Assert.True(((OperationCanceledException)error).CancellationToken == cancellation.Token, "Eltérő megszakítási token.");
    }

    [Fact]
    public async Task GetSecretAsync_SanitizesEnvironmentReaderFailure()
    {
        var sensitiveValue = NewSecret();
        var client = new FakeClient((_, _, _) => throw new InvalidOperationException());
        var provider = new BitwardenSecretProvider(client, _ => throw new InvalidOperationException(sensitiveValue));

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is SecretProviderException, "A környezetolvasó kivétele nem lett biztonságos hibára cserélve.");
        AssertSafe(error!, sensitiveValue);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task GetSecretAsync_RejectsEmptyIdentifierBeforeReadingToken()
    {
        var client = new FakeClient((_, _, _) => throw new InvalidOperationException());
        var environmentRead = false;
        var provider = new BitwardenSecretProvider(client, _ =>
        {
            environmentRead = true;
            return NewSecret();
        });

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.Empty));

        Assert.True(error is ArgumentException, "Az üres titokazonosító elfogadásra került.");
        Assert.True(error!.Message == "A titokazonosító nem lehet üres. (Parameter 'secretId')", "Eltérő azonosítóhiba-üzenet.");
        Assert.True(!environmentRead, "Hibás azonosító mellett környezeti változó olvasása történt.");
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task GetSecretAsync_DefaultAdapterIsLazyWhenTokenIsMissing()
    {
        ISecretProvider provider = new BitwardenSecretProvider(environmentReader: _ => null);

        var error = await Assert.ThrowsAsync<SecretProviderException>(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.Equal(SecretProviderError.MissingAccessToken, error.Error);
        Assert.True(error.InnerException is null, "A biztonságos hiba belső kivételt tartalmaz.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("debug")]
    [InlineData("off,bitwarden_core=trace")]
    public async Task GetSecretAsync_DefaultAdapterRefusesUnsafeNativeLoggingBeforeSdkUse(string? loggingFilter)
    {
        var accessToken = NewSecret();
        ISecretProvider provider = new BitwardenSecretProvider(environmentReader: name =>
            name == "CREDENTIALS_DIRECTORY" ? null :
            name == BitwardenSecretProvider.AccessTokenEnvironmentVariable ? accessToken : loggingFilter);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is SecretProviderException, "A natív SDK nem biztonságos naplózási beállítással elindult.");
        Assert.True(error!.Message == "A Bitwarden SDK használatához a RUST_LOG környezeti változó értéke off legyen.", "Eltérő naplózásibeállítás-hibaüzenet.");
        AssertSafe(error, accessToken);
    }

    [Fact]
    public async Task GetSecretAsync_CancellationDuringTokenReadDoesNotCallProvider()
    {
        using var cancellation = new CancellationTokenSource();
        var accessToken = NewSecret();
        var client = new FakeClient((_, _, _) => Task.FromResult<string?>(NewSecret()));
        var provider = new BitwardenSecretProvider(client, _ =>
        {
            cancellation.Cancel();
            return accessToken;
        });

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid(), cancellation.Token));

        Assert.True(error is OperationCanceledException, "A megszakított kérés nem megszakítási hibával zárult.");
        AssertSafe(error!, accessToken);
        Assert.Equal(0, client.Calls);
    }

    private static void AssertSafe(Exception error, params string[] sensitiveValues)
    {
        Assert.True(error.InnerException is null, "A biztonságos hiba belső kivételt tartalmaz.");
        Assert.True(error.Data.Count == 0, "A biztonságos hiba kiegészítő adatot tartalmaz.");
        foreach (var sensitiveValue in sensitiveValues)
        {
            Assert.True(!error.Message.Contains(sensitiveValue, StringComparison.Ordinal), "Titkos adat szivárgott a hibaüzenetbe.");
            Assert.True(!error.ToString().Contains(sensitiveValue, StringComparison.Ordinal), "Titkos adat szivárgott a kivételbe.");
        }
    }

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private sealed class FakeClient(Func<Guid, string, CancellationToken, Task<string?>> getSecret) : IBitwardenSecretClient
    {
        public int Calls { get; private set; }

        public Task<string?> GetSecretAsync(Guid secretId, string accessToken, CancellationToken cancellationToken)
        {
            Calls++;
            return getSecret(secretId, accessToken, cancellationToken);
        }
    }
}
