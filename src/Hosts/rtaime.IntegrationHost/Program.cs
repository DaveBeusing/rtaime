// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Client;

namespace rtaime.IntegrationHost;

public static class Program
{
	public static async Task<int> Main(string[] args)
	{
		try
		{
			var options = IntegrationGatewayOptions.Load(args);
			if (!options.Enabled)
				return 0;

			var password = string.IsNullOrWhiteSpace(options.Upstream.ClientCertificatePasswordEnvironmentVariable)
				? null
				: Environment.GetEnvironmentVariable(options.Upstream.ClientCertificatePasswordEnvironmentVariable);
			var bearer = string.IsNullOrWhiteSpace(options.Upstream.BearerTokenEnvironmentVariable)
				? null
				: Environment.GetEnvironmentVariable(options.Upstream.BearerTokenEnvironmentVariable);

			var transportOptions = new GrpcOperatorControlOptions
			{
				Endpoint = new Uri(options.Upstream.Endpoint, UriKind.Absolute),
				TrustMode = options.Upstream.TrustMode switch
				{
					IntegrationTrustMode.System => ExternalControlTrustMode.System,
					IntegrationTrustMode.PinnedServerCertificate => ExternalControlTrustMode.PinnedServerCertificate,
					IntegrationTrustMode.TestOnlyInsecure when options.TestMode => ExternalControlTrustMode.TestOnlyInsecure,
					IntegrationTrustMode.TestOnlyInsecure => throw new InvalidOperationException("Test-only insecure upstream trust requires testMode=true."),
					_ => throw new ArgumentOutOfRangeException()
				},
				TrustedServerCertificateThumbprint = options.Upstream.TrustedServerCertificateThumbprint,
				ClientCertificatePath = options.Upstream.ClientCertificatePath,
				ClientCertificateKeyPath = options.Upstream.ClientCertificateKeyPath,
				ClientCertificatePassword = password,
				BearerToken = bearer,
				ClientId = options.Upstream.ClientId,
				ClientName = options.Upstream.ClientName,
				ClientVersion = options.Upstream.ClientVersion,
				RequestTimeout = TimeSpan.FromMilliseconds(options.Upstream.RequestTimeoutMs)
			};

			await using var transport = new GrpcOperatorControlTransport(transportOptions);
			var client = new OperatorControlClient(transport);
			await using var gateway = new IntegrationGateway(options, client);
			foreach (var adapterOptions in options.Adapters.Where(adapter => adapter.Enabled))
				gateway.RegisterAdapter(IntegrationAdapterFactory.Create(adapterOptions, options));

			using var shutdown = new CancellationTokenSource();
			ConsoleCancelEventHandler handler = (_, eventArgs) =>
			{
				eventArgs.Cancel = true;
				shutdown.Cancel();
			};
			Console.CancelKeyPress += handler;
			try
			{
				await gateway.RunAsync(shutdown.Token).ConfigureAwait(false);
				return 0;
			}
			finally
			{
				Console.CancelKeyPress -= handler;
			}
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine($"rtaime.IntegrationHost failed: {exception.Message}");
			return 1;
		}
	}
}
