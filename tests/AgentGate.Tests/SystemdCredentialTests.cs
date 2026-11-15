using System.Security.Cryptography;
using AgentGate.Secrets;
using Xunit;

namespace AgentGate.Tests;

public sealed class SystemdCredentialTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-gate-credential-" + Guid.NewGuid().ToString("N"));
    private readonly string _token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private readonly string _envToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    private readonly List<string> _environmentReads = [];

    public SystemdCredentialTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialTakesPriorityWithoutReadingEnvironmentToken(bool credentialOnly)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "bitwarden-access-token"), _token);
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, ReadEnvironment, credentialOnly: credentialOnly);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is null, "A credential lekérése sikertelen.");
        Assert.True(client.MatchedToken, "Nem a credential token került továbbításra.");
        Assert.True(!_environmentReads.Contains(BitwardenSecretProvider.AccessTokenEnvironmentVariable), "Credential mellett env-token olvasás történt.");
    }

    [Fact]
    public async Task ReadsOnlyConfiguredCredentialFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "machine-token"), _token);
        await File.WriteAllTextAsync(Path.Combine(_directory, "bitwarden-access-token"), _envToken);
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, ReadEnvironment, credentialFileName: "machine-token");

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is null, "A megnevezett credential lekérése sikertelen.");
        Assert.True(client.MatchedToken, "Nem a megnevezett credential token került továbbításra.");
        Assert.True(!_environmentReads.Contains(BitwardenSecretProvider.AccessTokenEnvironmentVariable), "Credential mellett env-token olvasás történt.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrimsOnlyTrailingWhitespace(bool useCredential)
    {
        var expected = " \t" + _token;
        var raw = expected + " \r\n\t";
        await File.WriteAllTextAsync(Path.Combine(_directory, "bitwarden-access-token"), raw);
        var client = new FakeClient(expected);
        var provider = new BitwardenSecretProvider(client, name =>
            name == "CREDENTIALS_DIRECTORY" ? (useCredential ? _directory : null) : raw);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is null, "A token levágása sikertelen.");
        Assert.True(client.MatchedToken, "A token eleje vagy vége nem a szerződés szerint változott.");
    }

    [Theory]
    [InlineData("../outside-token")]
    [InlineData("nested/token")]
    [InlineData("nested\\token")]
    [InlineData("C:token")]
    [InlineData("tökén")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task RejectsNonSimpleCredentialFileName(string? fileName)
    {
        // A kilépési cél is saját ideiglenes könyvtárban marad.
        var root = Path.Combine(_directory, "credentials");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(_directory, "outside-token"), _token);
        if (fileName is "nested/token" or "nested\\token" or " " or "tökén" ||
            (!OperatingSystem.IsWindows() && fileName == "C:token"))
        {
            var rejectedPath = Path.Combine(root, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(rejectedPath)!);
            await File.WriteAllTextAsync(rejectedPath, _token);
        }
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, name =>
        {
            _environmentReads.Add(name);
            return name == "CREDENTIALS_DIRECTORY" ? root : _envToken;
        }, credentialFileName: fileName!);

        await AssertRejected(provider, client);
    }

    [Fact]
    public async Task RejectsAbsoluteCredentialFileNameEvenWhenItExists()
    {
        var path = Path.Combine(_directory, "outside-token");
        await File.WriteAllTextAsync(path, _token);
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, ReadEnvironment, credentialFileName: path);

        await AssertRejected(provider, client);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("relative")]
    [InlineData("nul")]
    public async Task RejectsInvalidRootWithoutEnvironmentFallback(string mode)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "bitwarden-access-token"), _token);
        var file = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(file, _token);
        var root = mode switch
        {
            "missing" => Path.Combine(_directory, "missing"),
            "file" => file,
            "relative" => Path.GetRelativePath(Environment.CurrentDirectory, _directory),
            "nul" => _directory + '\0',
            _ => mode
        };
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, name =>
        {
            _environmentReads.Add(name);
            return name == "CREDENTIALS_DIRECTORY" ? root : _envToken;
        });

        await AssertRejected(provider, client);
    }

    [Theory]
    [InlineData("outside")]
    [InlineData("inside")]
    [InlineData("root")]
    [InlineData("dangling")]
    public async Task RejectsCredentialSymlinks(string mode)
    {
        var root = Path.Combine(_directory, "credentials");
        Directory.CreateDirectory(root);
        var target = Path.Combine(mode == "inside" ? root : _directory, "target-token");
        if (mode != "dangling")
        {
            await File.WriteAllTextAsync(target, _token);
        }
        var credential = Path.Combine(root, "bitwarden-access-token");
        if (mode == "root")
        {
            await File.WriteAllTextAsync(credential, _token);
            var linkedRoot = Path.Combine(_directory, "linked-credentials");
            Directory.CreateSymbolicLink(linkedRoot, root);
            root = linkedRoot;
        }
        else
        {
            File.CreateSymbolicLink(credential, target);
        }
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, name =>
        {
            _environmentReads.Add(name);
            return name == "CREDENTIALS_DIRECTORY" ? root : _envToken;
        });

        await AssertRejected(provider, client);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("directory")]
    public async Task RejectsUnavailableCredentialWithoutEnvironmentFallback(string mode)
    {
        var path = Path.Combine(_directory, "bitwarden-access-token");
        if (mode == "directory")
        {
            Directory.CreateDirectory(path);
        }
        else if (mode != "missing")
        {
            await File.WriteAllTextAsync(path, mode == "empty" ? "" : " \r\n\t\u2003");
        }
        var client = new FakeClient(_envToken);
        var provider = new BitwardenSecretProvider(client, ReadEnvironment);

        await AssertRejected(provider, client);
    }

    [Fact]
    public async Task RejectsUnreadableCredentialWithoutEnvironmentFallback()
    {
        // A systemd célplatform Linux; nem root felhasználóval valódi EACCES-t vizsgálunk.
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Az olvasási jogosultság tesztjéhez nem root Unix futtatókörnyezet szükséges.");
        }
        var path = Path.Combine(_directory, "bitwarden-access-token");
        await File.WriteAllTextAsync(path, _token);
        var permissions = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var probe = await Record.ExceptionAsync(() => File.ReadAllTextAsync(path));
            Assert.True(probe is UnauthorizedAccessException, "A futtatókörnyezet nem biztosít valódi olvasási tiltást; root futtatás nem támogatott.");
            var client = new FakeClient(_envToken);
            var provider = new BitwardenSecretProvider(client, ReadEnvironment);

            await AssertRejected(provider, client);
        }
        finally
        {
            File.SetUnixFileMode(path, permissions);
        }
    }

    [Fact]
    public async Task NullCredentialRootUsesOnlyDevelopmentEnvironmentToken()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "bitwarden-access-token"), _token);
        var client = new FakeClient(_envToken);
        var provider = new BitwardenSecretProvider(client, name =>
        {
            _environmentReads.Add(name);
            return name == "CREDENTIALS_DIRECTORY" ? null : _envToken;
        });

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is null, "Az env-only fejlesztői forrás sikertelen.");
        Assert.True(client.MatchedToken, "Nem a fejlesztői env-token került továbbításra.");
        Assert.True(_environmentReads.SequenceEqual(new[] { "CREDENTIALS_DIRECTORY", BitwardenSecretProvider.AccessTokenEnvironmentVariable }), "Eltérő forrásválasztási sorrend.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeCredentialReadNeverCallsSdkOrEnvironmentToken(bool cancelInRootReader)
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "bitwarden-access-token"), _token);
        using var cancellation = new CancellationTokenSource();
        if (!cancelInRootReader)
        {
            cancellation.Cancel();
        }
        var client = new FakeClient(_token);
        var provider = new BitwardenSecretProvider(client, name =>
        {
            _environmentReads.Add(name);
            cancellation.Cancel();
            return name == "CREDENTIALS_DIRECTORY" ? _directory : _envToken;
        });

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid(), cancellation.Token));

        Assert.True(error is OperationCanceledException, "Nem megszakítási hiba keletkezett.");
        Assert.True(((OperationCanceledException)error!).CancellationToken == cancellation.Token, "Eltérő megszakítási token.");
        Assert.True(error.InnerException is null && error.Data.Count == 0, "Nem tiszta megszakítási hiba.");
        Assert.True(!error.ToString().Contains(_token, StringComparison.Ordinal), "Token szivárgott a megszakítási hibába.");
        Assert.True(client.Calls == 0, "Megszakítás után SDK-hívás történt.");
        Assert.True(!_environmentReads.Contains(BitwardenSecretProvider.AccessTokenEnvironmentVariable), "Megszakítás után env-token olvasás történt.");
        Assert.True(cancelInRootReader || _environmentReads.Count == 0, "Előre megszakított kérés környezetet olvasott.");
    }

    [Fact]
    public async Task IoFailureDoesNotExposeSensitiveFileName()
    {
        var fileName = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
        var client = new FakeClient(_envToken);
        var provider = new BitwardenSecretProvider(client, ReadEnvironment, credentialFileName: fileName);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is SecretProviderException, "A nyers I/O-hiba továbbjutott.");
        Assert.True(error!.InnerException is null && error.Data.Count == 0, "Az I/O-hiba részletei továbbjutottak.");
        Assert.True(!error.ToString().Contains(fileName, StringComparison.Ordinal), "A fájlnév kiszivárgott.");
        Assert.True(!error.ToString().Contains(_directory, StringComparison.Ordinal), "A fájlútvonal kiszivárgott.");
        Assert.True(client.Calls == 0, "Hiányzó fájl mellett SDK-hívás történt.");
        Assert.True(!_environmentReads.Contains(BitwardenSecretProvider.AccessTokenEnvironmentVariable), "I/O-hiba után env-token olvasás történt.");
    }

    [Fact]
    public async Task CredentialOnlyRejectsMissingRootWithoutReadingDevelopmentToken()
    {
        var client = new FakeClient(_envToken);
        string? Read(string name)
        {
            _environmentReads.Add(name);
            return name == "CREDENTIALS_DIRECTORY" ? null : _envToken;
        }
        var provider = new BitwardenSecretProvider(client, Read, credentialOnly: true);

        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));
        Assert.True(error is SecretProviderException { Error: SecretProviderError.MissingAccessToken },
            "A hiányzó gépi token hibatípusa elveszett.");
        await AssertRejected(provider, client);
    }

    private async Task AssertRejected(BitwardenSecretProvider provider, FakeClient client)
    {
        var error = await Record.ExceptionAsync(() => provider.GetSecretAsync(Guid.NewGuid()));

        Assert.True(error is SecretProviderException, "Nem biztonságos credentialhiba keletkezett.");
        Assert.True(error!.InnerException is null, "Belső kivétel szivárgott.");
        Assert.True(error.Data.Count == 0, "Kiegészítő adat szivárgott.");
        foreach (var sensitive in new[] { _directory, _token, _envToken })
        {
            Assert.True(!error.ToString().Contains(sensitive, StringComparison.Ordinal), "Bizalmas adat szivárgott a kivételbe.");
        }
        Assert.True(client.Calls == 0, "Hibás credential mellett SDK-hívás történt.");
        Assert.True(!_environmentReads.Contains(BitwardenSecretProvider.AccessTokenEnvironmentVariable), "Credentialhiba után env-token olvasás történt.");
    }

    private string? ReadEnvironment(string name)
    {
        _environmentReads.Add(name);
        return name == "CREDENTIALS_DIRECTORY" ? _directory : _envToken;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FakeClient(string expectedToken) : IBitwardenSecretClient
    {
        public int Calls { get; private set; }
        public bool MatchedToken { get; private set; }

        public Task<string?> GetSecretAsync(Guid secretId, string accessToken, CancellationToken cancellationToken)
        {
            Calls++;
            MatchedToken = accessToken == expectedToken;
            return Task.FromResult<string?>(Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)));
        }
    }
}
