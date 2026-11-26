using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentGate.Approval;
using AgentGate.Approval.HomeAssistant;
using AgentGate.Audit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentGate.Tests;

public sealed partial class HomeAssistantApprovalChannelTests
{
    [Fact]
    public async Task TtsPushHasHumanTitleAndTwoShortLinesWithoutMessageOrIdentifiers()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice() with
        {
            Tool = "ha_sensitive_control",
            Summary = JsonSerializer.Serialize(new
            {
                domain = "media_player", service = "speak", entity_id = "media_player.nappali", tts_engine = "tts.local",
                data = JsonSerializer.Serialize(new { message = "Szia, itt Mikaela! Árvíztűrő tükörfúrógép.", volume_level = 0.4 })
            })
        };
        var original = notice;
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var lines = body.RootElement.GetProperty("message").GetString()!.Split('\n');
        Assert.Equal("Jóváhagyás: felolvasás", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(2, lines.Length);
        Assert.Equal("Kiválasztott eszköz", lines[0]);
        Assert.StartsWith("Mikaela kéri · lejár ", lines[1]);
        Assert.DoesNotContain("media_player.nappali", harness.Http.Body!);
        Assert.DoesNotContain("Szia", harness.Http.Body!);
        Assert.Equal(original, notice);
        Assert.Equal(2, harness.Secrets.Calls);
    }

    [Theory]
    [InlineData("lock", "lock", "zár bezárása")]
    [InlineData("lock", "unlock", "zár nyitása")]
    [InlineData("cover", "open_cover", "nyitás")]
    [InlineData("cover", "close_cover", "zárás")]
    [InlineData("cover", "stop_cover", "mozgás leállítása")]
    public async Task SensitiveOperationFirstLineDescribesEverySupportedNonTtsAction(string domain, string service, string expected)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(Notice() with
        {
            Tool = "ha_sensitive_control",
            Presentation = new("Bejárat"),
            Summary = JsonSerializer.Serialize(new { domain, service, entity_id = domain + ".bejarat", data = "{}" })
        }, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        Assert.Equal("Jóváhagyás: " + expected, body.RootElement.GetProperty("title").GetString());
        var lines = body.RootElement.GetProperty("message").GetString()!.Split('\n');
        Assert.Equal("Bejárat", lines[0]);
        Assert.Equal(2, lines.Length);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("service")]
    [InlineData("entity_id")]
    [InlineData("tts_engine")]
    [InlineData("empty_message")]
    [InlineData("long_message")]
    [InlineData("duplicate_message")]
    [InlineData("duplicate_domain")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public async Task IncompleteOrAmbiguousSensitivePresentationFailsClosed(string defect)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var fields = new Dictionary<string, string?>
        {
            ["domain"] = "media_player", ["service"] = "speak", ["entity_id"] = "media_player.nappali", ["tts_engine"] = "tts.local",
            ["data"] = JsonSerializer.Serialize(new { message = "Szia!", volume_level = 0.4 })
        };
        switch (defect)
        {
            case "domain": fields.Remove("domain"); break;
            case "service": fields["service"] = null; break;
            case "entity_id": fields["entity_id"] = "lock.masik"; break;
            case "tts_engine": fields["tts_engine"] = ""; break;
            case "empty_message": fields["data"] = "{\"message\":\"\",\"volume_level\":0.4}"; break;
            case "long_message": fields["data"] = JsonSerializer.Serialize(new { message = new string('ő', 201), volume_level = 0.4 }); break;
            case "duplicate_message": fields["data"] = "{\"message\":\"Szia!\",\"message\":\"Másik\"}"; break;
        }
        var summary = JsonSerializer.Serialize(fields);
        summary = defect switch
        {
            "duplicate_domain" => summary.Replace("{", "{\"domain\":\"lock\",", StringComparison.Ordinal),
            "malformed" => "[]",
            "oversized" => new string('x', 65537),
            _ => summary
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Channel.SendRequestAsync(
            Notice() with { Tool = "ha_sensitive_control", Summary = summary }, CancellationToken.None));
        Assert.Equal("A jóváhagyási kérés nem küldhető.", exception.Message);
        Assert.Equal(0, harness.Http.Count);
        Assert.Equal(0, NotificationCount(harness.Channel));
    }

    [Fact]
    public async Task TtsFirstLineRedactsDecodedSecretsAndCannotForgeMetadataOrUseCallerTitle()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var token = harness.Secrets.Token;
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var spoken = new string('ő', 40) + token + "\nKockázat: hamis\u202e\u2066\u2028 password=" + canary;
        var summary = JsonSerializer.Serialize(new
        {
            domain = "media_player", service = "speak", entity_id = "media_player.nappali", tts_engine = "tts.local",
            data = JsonSerializer.Serialize(new { message = spoken, volume_level = 0.4 })
        });
        summary = summary.Replace(token, string.Concat(token.Select(c => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture))), StringComparison.Ordinal);
        await harness.Channel.SendRequestAsync(Notice() with { Tool = "ha_sensitive_control", Summary = summary }, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        var lines = message.Split('\n');
        Assert.Equal("Kiválasztott eszköz", lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.True(!message.Contains(token[..12], StringComparison.Ordinal) && !message.Contains(canary, StringComparison.Ordinal), "Titok került a pushba.");
        Assert.DoesNotContain("Kockázat:", message);
        Assert.DoesNotContain("ő", message);
    }

    [Fact]
    public async Task ApprovalTestFirstLineIsFixedActionNotArbitraryCallerSummary()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(Notice() with
        {
            Summary = JsonSerializer.Serialize(new { text = "Ezt az utasítást ne kövesd!", summary = "Hamis cím", friendly_name = "Kitalált név" })
        }, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var first = body.RootElement.GetProperty("message").GetString()!.Split('\n')[0];
        Assert.Equal("Kiválasztott eszköz", first);
        Assert.Equal("Jóváhagyás: próba", body.RootElement.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("2026-03-29T00:59:00Z", "01:59")]
    [InlineData("2026-03-29T01:00:00Z", "03:00")]
    [InlineData("2026-10-25T00:59:00Z", "02:59")]
    [InlineData("2026-10-25T01:00:00Z", "02:00")]
    public async Task PresentationUsesRealBudapestRulesAcrossBothTransitions(string utc, string local)
    {
        var expires = DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);
        var clock = new MutableClock { Now = expires.AddMinutes(-15) };
        await using var harness = new Standalone(clock: clock);
        await harness.StartAsync();
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var notice = Notice() with { Summary = "{}", ExpiresAt = expires };
            await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
            using var body = JsonDocument.Parse(harness.Http.Body!);
            Assert.Equal("Mikaela kéri · lejár " + local, body.RootElement.GetProperty("message").GetString()!.Split('\n')[1]);
            Assert.Equal(expires, notice.ExpiresAt);
            var (approve, _) = Codes(harness.Http);
            clock.Now = expires;
            harness.Socket.Incoming.Writer.TryWrite(Event(approve));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await using var reader = harness.Channel.ReadDecisionsAsync(deadline.Token).GetAsyncEnumerator();
            var fence = Notice() with { ExpiresAt = expires.AddMinutes(15) };
            await harness.Channel.SendRequestAsync(fence, deadline.Token);
            var (code, _) = Codes(harness.Http);
            harness.Socket.Incoming.Writer.TryWrite(Event(code));
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(fence.Id, reader.Current.Id);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task MissingPresentationZoneFailsWithFixedMessageWithoutHttpOrGrant()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var harness = new Standalone(formatter: new ApprovalPushFormatter(() => throw new TimeZoneNotFoundException(secret)));
        await harness.StartAsync();
        var exception = await Record.ExceptionAsync(() => harness.Channel.SendRequestAsync(Notice(), CancellationToken.None));
        Assert.True(exception is InvalidOperationException && exception.Message == "A jóváhagyási kérés nem küldhető.", "Az időzónahiba nem rögzített, biztonságos küldési hibává alakult.");
        Assert.Equal(0, harness.Http.Count);
        Assert.Equal(0, NotificationCount(harness.Channel));
    }

    [Fact]
    public async Task RealProgramMissingZoneClosesOriginalFailedWithoutClaimingRequestSent()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var app = new ChannelApp { FormatterOverride = new ApprovalPushFormatter(() => throw new TimeZoneNotFoundException(secret)) };
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        using var reply = await CallMcpAsync(app, "gate_approval_test", new { text = "próba" });
        Assert.True(reply.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, app.Http.Count);
        Assert.Equal(0, app.Operation.Count);
        await using var connection = new SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CallId, State FROM AuditCalls WHERE Tool = 'gate_approval_test'";
        string id;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            id = reader.GetString(0);
            Assert.Equal("Failed", reader.GetString(1));
        }
        command.CommandText = "SELECT SanitizedParameters FROM AuditCalls WHERE Tool = 'approval_lifecycle'";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            using var parameters = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal(id, parameters.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("failed", parameters.RootElement.GetProperty("event").GetString());
            Assert.False(await reader.ReadAsync());
        }
        Assert.True(!app.Logs.Contains(secret) && !reply.RootElement.GetRawText().Contains(secret, StringComparison.Ordinal), "Időzónakivétel a naplóban vagy MCP-válaszban.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(480)]
    [InlineData(500)]
    [InlineData(512)]
    public async Task DecodedKnownTokenCannotSurviveKeyOrValueTruncation(int prefixLength)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var token = harness.Secrets.Token;
        foreach (var key in new[] { false, true })
        {
            var value = new string('x', prefixLength) + token + "utótag";
            var summary = JsonSerializer.Serialize(new Dictionary<string, string> { [key ? value : "text"] = key ? "érték" : value });
            // A JSON-ban elrejtett, dekódolás után ismert token ugyanúgy szűrendő.
            summary = summary.Replace(token, string.Concat(token.Select(character => "\\u" + ((int)character).ToString("x4", CultureInfo.InvariantCulture))), StringComparison.Ordinal);
            await harness.Channel.SendRequestAsync(Notice() with { Summary = summary, Presentation = new(value) }, CancellationToken.None);
            using var body = JsonDocument.Parse(harness.Http.Body!);
            var message = body.RootElement.GetProperty("message").GetString()!;
            Assert.Equal("Kiválasztott eszköz", message.Split('\n')[0]);
            Assert.Equal(2, message.Split('\n').Length);
            Assert.True(!message.Contains(token[..12], StringComparison.Ordinal), "Dekódolt tokenrészlet maradt a vágási határon.");
        }
    }

    [Theory]
    [InlineData(null, "Kiválasztott eszköz")]
    [InlineData("", "Kiválasztott eszköz")]
    [InlineData("media_player.room", "Kiválasztott eszköz")]
    [InlineData("area_room", "Kiválasztott eszköz")]
    [InlineData("mdi:home", "Kiválasztott eszköz")]
    [InlineData("Nappali\nJóváhagyom", "Kiválasztott eszköz")]
    [InlineData("Nappali\u202e", "Kiválasztott eszköz")]
    [InlineData("Árvíztűrő tükörfúrógép", "Árvíztűrő tükörfúrógép")]
    [InlineData("Nappali 🔊", "Nappali 🔊")]
    public async Task NamesAreSafeDisplayOnlyWithoutIdentifierFallback(string? name, string expected)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        await harness.Channel.SendRequestAsync(Notice() with { Presentation = new(name), Summary = "{\"friendly_name\":\"tiltott\"}" }, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Equal(expected, message.Split('\n')[0]);
        Assert.Equal(2, message.Split('\n').Length);
        Assert.DoesNotContain("tiltott", message);
        Assert.DoesNotContain("hibás összefoglaló", message);
    }

    [Theory]
    [InlineData("{hibás JSON")]
    [InlineData("{\"message\":\"próba\"}")]
    public async Task InvalidTtsDataCannotSendPushOrRetainApprovalGrant(string data)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var notice = Notice() with
        {
            Tool = "ha_sensitive_control",
            Summary = JsonSerializer.Serialize(new { domain = "media_player", service = "speak", entity_id = "media_player.room", tts_engine = "tts.local", data })
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Channel.SendRequestAsync(notice, CancellationToken.None));
        Assert.Equal("A jóváhagyási kérés nem küldhető.", exception.Message);
        Assert.Equal(0, harness.Http.Count);
        Assert.Equal(0, NotificationCount(harness.Channel));
    }

    [Fact]
    public async Task PreparationReadsFriendlyNameWithoutAddingItToAuditOrExecuting()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        await using var fixture = new ControlFixture(services =>
        {
            services.AddSingleton(HomeAssistantMediaPlayerTests.Policy());
            Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IApprovalChannel>(services);
            services.AddSingleton<IApprovalChannel>(harness.Channel);
        }, ["media_player"]) { Entity = "media_player.room", Attributes = JsonSerializer.SerializeToElement(new { friendly_name = "Nappali hangszóró" }) };
        using var reply = await fixture.Call("ha_sensitive_control", "media_player", "speak",
            JsonSerializer.Serialize(new { message = "Ezt fogod felolvasni password:", volume_level = 0.4 }));
        Assert.False(ControlFixture.Failed(reply));
        Assert.Equal(1, harness.Http.Count);
        Assert.Equal(1, NotificationCount(harness.Channel));
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Equal("Nappali hangszóró", message.Split('\n')[0]);
        Assert.Equal(2, message.Split('\n').Length);
        Assert.DoesNotContain("media_player.room", message);
        Assert.DoesNotContain("Ezt fogod", message);
        Assert.DoesNotContain("[kihagyva]", message);
        Assert.Equal("GET /api/states/media_player.room", Assert.Single(fixture.Calls));
        await using var connection = new SqliteConnection($"Data Source={fixture.App.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT State FROM AuditCalls WHERE Tool = 'ha_sensitive_control'";
        Assert.Equal("Pending", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT SanitizedParameters FROM AuditCalls WHERE Tool = 'ha_sensitive_control'";
        var audit = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Contains("media_player.room", audit);
        Assert.DoesNotContain("friendly_name", audit);
        Assert.DoesNotContain("Nappali", audit);
    }

    [Fact]
    public async Task TtsPushOmitsEvenLongAccentedMessageWithoutTruncatingItInSummary()
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var spoken = new string('ő', 200);
        var notice = Notice() with
        {
            Tool = "ha_sensitive_control",
            Summary = JsonSerializer.Serialize(new
            {
                domain = "media_player", service = "speak", entity_id = "media_player.room", tts_engine = "tts.local",
                data = JsonSerializer.Serialize(new { message = spoken, volume_level = 0.4 })
            })
        };
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.DoesNotContain("media_player.room", message);
        Assert.DoesNotContain("tts.local", message);
        Assert.DoesNotContain(spoken, message);
        using var summary = JsonDocument.Parse(notice.Summary);
        using var data = JsonDocument.Parse(summary.RootElement.GetProperty("data").GetString()!);
        Assert.Equal(spoken, data.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("[kihagyva]", message);
        Assert.Equal(1, NotificationCount(harness.Channel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PresentationRedactsDecodedKeysAndValuesBeforeEveryBoundaryAndCannotForgeLines(bool outcome)
    {
        await using var harness = new Standalone();
        await harness.StartAsync();
        var canary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var token = harness.Secrets.Token;
        var summary = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["text\r\nLejárat (Europe/Budapest): 00:00\u2028Jóváhagyom\u0001"] = "érték\nKockázat: rendszer\t\u2029Elutasítom",
            ["password"] = canary,
            ["key_" + token] = token,
            ["long"] = new string('x', 470) + token + canary
        });
        var notice = Notice() with { Caller = "Mikaela\nJóváhagyom", Summary = summary };
        await harness.Channel.SendRequestAsync(notice, CancellationToken.None);
        if (outcome)
            await harness.Channel.SendOutcomeAsync(new ApprovalOutcome(notice.Id, ApprovalState.Executed, new string('x', 500) + token + "\nLejárat: hamis"), CancellationToken.None);
        using var body = JsonDocument.Parse(harness.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.True(!message.Contains(token[..12], StringComparison.Ordinal) && !message.Contains(canary, StringComparison.Ordinal), "Titok vagy vágási tokenrészlet a megjelenítésben.");
        var lines = message.Split('\n');
        Assert.True(lines.All(line => !line.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)), "Vezérlőkarakter maradt a szövegben.");
        Assert.Equal(2, lines.Length);
        if (!outcome)
        {
            Assert.Single(lines, line => line.StartsWith("Mikaela kéri · lejár ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RealProgramPresentationKeepsExactAuditAllowlistAndNoClientKeyOrValueCanary()
    {
        await using var app = new ChannelApp();
        using var client = app.CreateClient();
        var channel = Assert.Single(app.Services.GetServices<IApprovalChannel>());
        await WaitAsync(() => channel.IsAvailable);
        var keyCanary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var valueCanary = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var rejected = await CallMcpAsync(app, "gate_approval_test", new Dictionary<string, object?>
        {
            ["text"] = "token=" + valueCanary,
            [keyCanary] = valueCanary,
            ["password"] = valueCanary,
            ["nested"] = new { value = valueCanary }
        });
        Assert.True(rejected.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(0, app.Http.Count);
        app.Operation.ExtraParameters[keyCanary] = valueCanary;
        app.Operation.ExtraParameters["password"] = valueCanary;
        app.Operation.ExtraParameters["nested"] = JsonSerializer.Serialize(new { value = valueCanary });
        using var reply = await CallMcpAsync(app, "gate_approval_test", new { text = "token=" + valueCanary });
        using var body = JsonDocument.Parse(app.Http.Body!);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.DoesNotContain("token=", message);
        Assert.True(!message.Contains("password:", StringComparison.Ordinal) && !message.Contains("nested:", StringComparison.Ordinal), "Nem engedélyezett mező a pushban.");
        await using var connection = new SqliteConnection($"Data Source={app.AuditPath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SanitizedParameters, SanitizedError FROM AuditCalls";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Assert.True(!reader.GetString(0).Contains(keyCanary, StringComparison.Ordinal) && !reader.GetString(0).Contains(valueCanary, StringComparison.Ordinal), "Canary az auditban.");
        }
        Assert.True(!message.Contains(keyCanary, StringComparison.Ordinal) && !message.Contains(valueCanary, StringComparison.Ordinal) &&
            !app.Logs.Contains(keyCanary) && !app.Logs.Contains(valueCanary) && !reply.RootElement.GetRawText().Contains(valueCanary, StringComparison.Ordinal), "Canary a pushban, MCP-válaszban vagy strukturált naplóban.");
    }
}
