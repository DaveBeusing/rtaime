// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.AppHost;

public sealed class ApplicationEvidencePublicationException(string targetPath, Exception innerException)
	: IOException($"Atomic startup evidence publication failed for '{targetPath}' (native error {innerException.HResult & 0xffff}): {innerException.Message}", innerException)
{
	public string TargetPath { get; } = targetPath;
}
