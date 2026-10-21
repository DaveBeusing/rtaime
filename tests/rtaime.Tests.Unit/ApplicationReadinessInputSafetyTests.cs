// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using System.Text.Json;
using rtaime.AppHost;

namespace rtaime.Tests.Unit;

public sealed class ApplicationReadinessInputSafetyTests
{
    [Fact]
    public void Corrupt_unbounded_or_impossible_process_readiness_cannot_qualify()
    {
        var valid = JsonSerializer.Serialize(new
        {
            processId = 42,
            state = "READY",
            health = "HEALTHY",
            controlEndpoint = "rtaime.test.control",
            runtimeEndpoint = "rtaime.test.runtime",
            aiEndpoint = "rtaime.test.ai",
            runtimeSupervision = new { state = "HEALTHY", processId = 7001 },
            aiSupervision = new { state = "HEALTHY", processId = 7002 }
        });
        Assert.True(ApplicationReadinessEvidence.TryParse(valid, out var qualified));
        Assert.Equal(42, qualified!.ProcessId);

        string[] invalid =
        [
            "{", "{}", "[]", "null",
            new string('x', ApplicationReadinessEvidence.MaximumDocumentChars + 1),
            valid.Replace("\"processId\":42", "\"processId\":0", StringComparison.Ordinal),
            valid.Replace("\"processId\":42", "\"processId\":2147483648", StringComparison.Ordinal),
            valid.Replace("\"processId\":7001", "\"processId\":0", StringComparison.Ordinal),
            valid.Replace("\"processId\":7002", "\"processId\":4294967297", StringComparison.Ordinal),
            valid.Replace("\"runtimeSupervision\":{", "\"runtimeSupervision\":[", StringComparison.Ordinal)
        ];

        foreach (var payload in invalid)
        {
            Assert.False(ApplicationReadinessEvidence.TryParse(payload, out var observation));
            Assert.Null(observation);
        }
    }
}
