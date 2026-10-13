// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text;
using rtaime.Core;

namespace rtaime.AppHost;

internal sealed class BoundedProcessDiagnosticLog(string path)
{
	internal const long MaximumFileBytes = 2L * 1024 * 1024;
	private readonly object _gate = new();

	public void Append(string line)
	{
		// Redact before truncation so clipping cannot expose part of a credential.
		var sanitized = DiagnosticRedactor.RedactText(line);
		if (sanitized.Length > 16384) sanitized = sanitized[..16384] + " [truncated]";
		var record = sanitized + Environment.NewLine;
		lock (_gate)
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
				if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(record) > MaximumFileBytes)
					File.Move(path, path + ".previous", overwrite: true);
				File.AppendAllText(path, record, new UTF8Encoding(false));
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				// Diagnostic failure must not break process supervision or stream drainage.
			}
		}
	}
}
