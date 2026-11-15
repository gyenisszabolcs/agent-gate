namespace AgentGate.Secrets;

public sealed class SecretSelfTestService(
    IConfiguration configuration,
    IServiceProvider services,
    ILogger<SecretSelfTestService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configuredId = configuration["Secrets:SelfTestSecretId"];
        if (configuredId is null)
        {
            return;
        }
        if (!Guid.TryParse(configuredId, out var id) || id == Guid.Empty)
        {
            logger.LogWarning("A Bitwarden önteszt sikertelen. Hibatípus: Configuration.");
            return;
        }
        using var cancellation = new CancellationTokenSource();
        // Külön munkafeladat kell: a szolgáltató szinkron belépése sem késleltetheti a határidő jelzését.
        // A visszaadott titkot nem őrizzük meg a feladat eredményében.
        var work = Task.Run(async () =>
        {
            _ = await services.GetRequiredService<ISecretProvider>().GetSecretAsync(id, cancellation.Token);
        }, cancellation.Token);
        try
        {
            await work.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            if (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("A Bitwarden önteszt sikeres.");
            }
        }
        catch (TimeoutException)
        {
            logger.LogWarning("A Bitwarden önteszt sikertelen. Hibatípus: Timeout.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A leállítás nem önteszthiba; a saját munka felszabadítását alább megvárjuk.
        }
        catch (Exception error)
        {
            var errorType = error is SecretProviderException providerError ? providerError.Error switch
            {
                SecretProviderError.MissingAccessToken => "MissingAccessToken",
                SecretProviderError.NotFound => "NotFound",
                SecretProviderError.AuthenticationFailed => "AuthenticationFailed",
                SecretProviderError.UnsafeDiagnostics => "UnsafeDiagnostics",
                _ => "ProviderFailure"
            } : "ProviderFailure";
            logger.LogWarning("A Bitwarden önteszt sikertelen. Hibatípus: {ErrorType}.", errorType);
        }
        finally
        {
            try
            {
                await cancellation.CancelAsync();
            }
            catch (Exception)
            {
                // Helyettesített szolgáltató megszakítási visszahívása sem szivárogtathat kivételt.
            }
            try
            {
                // A natív átvitel nem szakítható meg erőszakkal. A feladat tulajdonosa maradunk;
                // a host StopAsync határideje közben lejárhat, de a késői hibát is megfigyeljük.
                await work;
            }
            catch (Exception)
            {
                // Az eredményt már jeleztük; nincs ismételt napló és nincs nyers kivétel.
            }
        }
    }
}
