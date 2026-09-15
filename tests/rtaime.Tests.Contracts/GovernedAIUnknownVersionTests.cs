using rtaime.AI.Contracts;
using rtaime.Core;

namespace rtaime.Tests.Contracts;

public sealed class GovernedAIUnknownVersionTests
{
    [Fact]
    public void New_provider_descriptor_fails_closed_for_unknown_AI_contract_version()
    {
        var unknown = new CompatibilityVersion(99, 0);

        Assert.Throws<NotSupportedException>(() => new InferenceProviderDescriptor(
            unknown,
            InferenceProviderId.New(),
            "Unknown version provider",
            InferenceProviderState.Ready,
            null,
            Array.Empty<InferenceCapabilityDescriptor>(),
            Array.Empty<InferenceModelPackageDescriptor>()));
    }
}
