using System.Security.Cryptography;
using AgentGate.Secrets;
using Xunit;

namespace AgentGate.Tests;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("ToKeN")]
    [InlineData("PASSWORD")]
    [InlineData("Secret")]
    [InlineData("KEY")]
    [InlineData("aUtHoRiZaTiOn")]
    [InlineData("access_token")]
    [InlineData("refreshToken")]
    [InlineData("clientSecret")]
    [InlineData("db.password")]
    [InlineData("api-key")]
    [InlineData("X-API-Key")]
    [InlineData("ApiKey")]
    [InlineData("apikey")]
    [InlineData("PRIVATEKEY")]
    [InlineData("authorizationHeader")]
    [InlineData("sshKey")]
    [InlineData("SESSIONKEY")]
    [InlineData("consumer_key")]
    public void RecognizesSensitiveParameterNames(string name)
    {
        var input = new Dictionary<string, string?> { [name] = NewSecret() };
        var result = SecretRedactor.RedactParameters(input);
        Assert.True(result[name] == "[REDACTED]", "Az érzékeny név felismerése hiányzik.");
        Assert.True(SecretRedactor.RedactText($"{name}={input[name]}") == $"{name}=[REDACTED]",
            "A szöveges névfelismerés eltér a paraméteres felismeréstől.");
    }

    [Fact]
    public void RedactsPlainTextKeyValue()
    {
        var secret = NewSecret();
        var input = $"status=ok ToKeN={secret} name=lamp";
        var result = SecretRedactor.RedactText(input);

        Assert.True(result == "status=ok ToKeN=[REDACTED] name=lamp", "A szöveges érték nincs kitakarva.");
        Assert.True(input.Contains(secret, StringComparison.Ordinal), "A bemenet megváltozott.");
    }

    [Theory]
    [InlineData("\"password\" : \"{0}\"")]
    [InlineData("'clientSecret'='{0}'")]
    [InlineData("api-key = \"{0}\"")]
    [InlineData("{{\"name\":\"lamp\",\"nested\":{{\"refreshToken\":\"{0}\"}}}}")]
    public void RedactsQuotedAndJsonValues(string template)
    {
        var secret = NewSecret() + " escaped \\\" single ' slash \\\\ end";
        var encoded = template.Contains("'{0}'", StringComparison.Ordinal)
            ? secret.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
            : System.Text.Json.JsonEncodedText.Encode(secret).ToString();
        var input = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, encoded);
        var expected = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, "[REDACTED]");

        var result = SecretRedactor.RedactText(input);
        Assert.False(result == "[REDACTED]", "A rövid bemenet teljes kitakarási tartalékágra került.");
        Assert.True(result == expected, "Az idézett érték nincs teljesen kitakarva.");
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("bAsIc")]
    public void RedactsWholeAuthorizationValueAmongAdjacentSecrets(string scheme)
    {
        var first = NewSecret();
        var second = NewSecret();
        var third = NewSecret();
        var input = $"token={first}&password={second}; Authorization: {scheme} {third}\r\nname=lamp";

        Assert.True(SecretRedactor.RedactText(input) ==
            "token=[REDACTED]&password=[REDACTED]; Authorization: [REDACTED]\r\nname=lamp",
            "Nem minden szomszédos titok lett kitakarva.");
    }

    [Theory]
    [InlineData("Bearer", "token")]
    [InlineData("Basic", "password")]
    public void PreservesHarmlessSchemeWithoutSwallowingAdjacentSecret(string scheme, string sensitiveName)
    {
        var secret = NewSecret();
        var input = $"name={scheme} {sensitiveName}={secret}";
        var result = SecretRedactor.RedactText(input);

        Assert.True(result == $"name={scheme} {sensitiveName}=[REDACTED]",
            "Az ártalmatlan sémaérték elnyelte a következő érzékeny mezőt.");
        Assert.False(result!.Contains(secret, StringComparison.Ordinal), "A szomszédos titok megmaradt.");
    }

    [Theory]
    [InlineData("Bearer", "\"")]
    [InlineData("Bearer", "'")]
    [InlineData("Basic", "\"")]
    [InlineData("Basic", "'")]
    public void PreservesQuotedPasswordBoundaryAfterSensitiveScheme(string scheme, string quote)
    {
        var secret = NewSecret();
        var result = SecretRedactor.RedactText($"token={scheme} password={quote}{secret}{quote}");

        Assert.False(result!.Contains(secret, StringComparison.Ordinal), "A következő idézett titok kiszivárgott.");
        Assert.True(result == $"token=[REDACTED] password={quote}[REDACTED]{quote}",
            "A séma elfogyasztotta a következő hozzárendelés prefixét.");
    }

    [Theory]
    [InlineData("Bearer", "password", "=", "\"", "&")]
    [InlineData("Basic", "clientSecret", ":", "'", ";")]
    [InlineData("Bearer", "access_token", " = ", "\"", ",")]
    [InlineData("Basic", "db.password", "=\t", "'", "}")]
    [InlineData("Bearer", "api-key", ": ", "\"", "]")]
    [InlineData("Bearer", "password", "= ", "", ";")]
    [InlineData("Basic", "password", "=\t", "", "&")]
    public void RedactsFollowingFieldsWithAttachedDelimiters(string scheme, string name,
        string separator, string quote, string delimiter)
    {
        var secret = NewSecret();
        var nextSecret = NewSecret();
        var input = $"token={scheme}\t{name}{separator}{quote}{secret}{quote}{delimiter}secret={nextSecret}";
        var result = SecretRedactor.RedactText(input);

        Assert.False(result!.Contains(secret, StringComparison.Ordinal), "A következő mező titka megmaradt.");
        Assert.False(result.Contains(nextSecret, StringComparison.Ordinal), "A további mező titka megmaradt.");
        Assert.True(result == $"token=[REDACTED]\t{name}{separator}{quote}[REDACTED]{quote}{delimiter}secret=[REDACTED]",
            "A mezőhatár vagy a csatolt elválasztó megváltozott.");
        Assert.True(SecretRedactor.RedactText(result) == result, "A mezőhatár javítása nem idempotens.");
    }

    [Theory]
    [InlineData("Bearer", "")]
    [InlineData("Bearer", "=")]
    [InlineData("Basic", "=")]
    [InlineData("Basic", "==")]
    public void RedactsRealCredentialsIncludingPadding(string scheme, string padding)
    {
        var credential = "cred" + NewSecret() + padding;
        var secret = NewSecret();
        foreach (var suffix in new[] { "", $" password=\"{secret}\"", $";password='{secret}'",
            $" \"password\":\"{secret}\"", $" 'password'='{secret}'", " name=lamp" })
        {
            var result = SecretRedactor.RedactText($"Authorization: {scheme} {credential}{suffix}");
            Assert.False(result!.Contains(credential, StringComparison.Ordinal), "A valódi credential megmaradt.");
            Assert.False(result.Contains(secret, StringComparison.Ordinal), "Az Authorization utáni titok megmaradt.");
            Assert.True(result == "Authorization: [REDACTED]" +
                suffix.Replace(secret, "[REDACTED]", StringComparison.Ordinal), "A credential nincs teljesen kitakarva.");
        }
    }

    [Theory]
    [InlineData("Bearer", "=")]
    [InlineData("Bearer", "==")]
    [InlineData("Basic", "=")]
    [InlineData("Basic", "==")]
    public void FailsClosedForAmbiguousPaddedAuthorizationFollowedByProse(string scheme, string padding)
    {
        var credential = "cred" + NewSecret() + padding;
        var result = SecretRedactor.RedactText($"Authorization: {scheme} {credential} completed");

        Assert.False(result!.Contains(credential, StringComparison.Ordinal), "Az ambiguus credential megmaradt.");
        Assert.True(result == "[REDACTED]", "Az ambiguus Authorization nem zártan hibázik.");
    }

    [Fact]
    public void RedactsSecretAfterLongSensitiveSchemeChain()
    {
        var prefix = string.Concat(Enumerable.Repeat("token=Bearer password=Basic\t", 2_000));
        var expected = string.Concat(Enumerable.Repeat("token=[REDACTED] password=[REDACTED]\t", 2_000));
        var secret = NewSecret();
        var result = SecretRedactor.RedactText($"{prefix}password=\"{secret}\"");

        Assert.False(result!.Contains(secret, StringComparison.Ordinal), "A hosszú sémalánc végén a titok megmaradt.");
        Assert.True(result == $"{expected}password=\"[REDACTED]\"", "A hosszú sémalánc mezőhatára hibás.");
        Assert.True(SecretRedactor.RedactText(result) == "[REDACTED]",
            "A mérethatárt meghaladó kitakart lánc nem zártan hibázik.");
    }

    [Theory]
    [InlineData(65_535)]
    [InlineData(65_536)]
    [InlineData(65_537)]
    public void AppliesSizeBoundaryToSensitiveSchemeAndQuotedField(int length)
    {
        var secret = NewSecret();
        var tail = $" token=Bearer password=\"{secret}\"";
        var prefix = new string('x', length - tail.Length);
        var result = SecretRedactor.RedactText(prefix + tail);

        Assert.False(result!.Contains(secret, StringComparison.Ordinal), "A mérethatárnál titok maradt.");
        Assert.True(result == (length > 65_536 ? "[REDACTED]" :
            prefix + " token=[REDACTED] password=\"[REDACTED]\""), "A mérethatár kezelése hibás.");
    }

    [Fact]
    public void RedactsSecretAfterLongHarmlessSchemeChain()
    {
        var prefix = string.Concat(Enumerable.Repeat("name=Bearer name=Basic\t", 2_000));
        var secret = NewSecret();
        var result = SecretRedactor.RedactText($"{prefix}password={secret}");

        Assert.True(result == $"{prefix}password=[REDACTED]",
            "A hosszú ártalmatlan sémalánc után a titok nincs külön kitakarva.");
        Assert.False(result!.Contains(secret, StringComparison.Ordinal), "A lánc végén a titok megmaradt.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("name=lamp count=3 monkey=banana keyboard=hu layout=keynote")]
    [InlineData("{\"name\":\"lamp\",\"count\":3}")]
    [InlineData("token=\nname=lamp")]
    public void PreservesEmptyAndHarmlessText(string? input)
    {
        Assert.True(SecretRedactor.RedactText(input) == input, "Az ártalmatlan szöveg megváltozott.");
    }

    [Fact]
    public void ReturnsIndependentReadOnlySnapshotAndPreservesHarmlessParameters()
    {
        var input = new Dictionary<string, string?>
        {
            ["name"] = "lamp",
            ["monkey"] = "banana",
            ["keyboard"] = "hu",
            ["empty"] = "",
            ["missing"] = null,
            ["TOKEN"] = NewSecret(),
            ["token"] = NewSecret(),
            ["password"] = "",
            ["secret"] = null
        };
        var result = SecretRedactor.RedactParameters(input);
        foreach (var name in new[] { "name", "monkey", "keyboard", "empty", "missing" })
        {
            Assert.True(input[name] == result[name], "Az ártalmatlan paraméter megváltozott.");
        }
        foreach (var name in new[] { "TOKEN", "token", "password", "secret" })
        {
            Assert.True(result[name] == "[REDACTED]", "Az érzékeny paraméter nincs kitakarva.");
        }
        input["name"] = "changed";
        Assert.Equal("lamp", result["name"]);
        Assert.True(((IDictionary<string, string?>)result).IsReadOnly);
        Assert.Empty(SecretRedactor.RedactParameters(new Dictionary<string, string?>()));
        Assert.Throws<ArgumentNullException>(() => SecretRedactor.RedactParameters(null!));
    }

    [Fact]
    public void RedactionIsIdempotentForMultipleJsonSecrets()
    {
        var input = $"{{\"token\":\"{NewSecret()}\",\"password\":\"{NewSecret()}\",\"name\":\"lamp\"}}";
        var once = SecretRedactor.RedactText(input);
        Assert.True(once == "{\"token\":\"[REDACTED]\",\"password\":\"[REDACTED]\",\"name\":\"lamp\"}",
            "Az egymás melletti JSON-titkok kitakarása hiányos.");
        Assert.True(SecretRedactor.RedactText(once) == once, "A kitakarás nem idempotens.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailsClosedForOversizedText(bool hostile)
    {
        var input = hostile ? "password=\"" + new string('\\', 100_000) + NewSecret() : new string('a', 100_000);
        Assert.True(SecretRedactor.RedactText(input) == "[REDACTED]", "A túlméretes szöveg nem zártan hibázik.");
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void RedactsSensitiveParameterWithoutChangingInput()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var input = new Dictionary<string, string?> { ["token"] = secret, ["name"] = "lamp" };
        var result = SecretRedactor.RedactParameters(input);

        Assert.True(result["token"] == "[REDACTED]", "Az érzékeny paraméter nincs kitakarva.");
        Assert.True(input["token"] == secret, "A bemenet megváltozott.");
        Assert.Equal("lamp", result["name"]);
        Assert.NotSame(input, result);
    }
}
