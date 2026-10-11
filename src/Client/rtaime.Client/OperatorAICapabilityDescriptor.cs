// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
namespace rtaime.Client;

/// <summary>Provider-advertised evidence; no model installation or production qualification is implied.</summary>
public sealed record OperatorAIModelDescriptor(string ModelVersion, string InputContract, string OutputContract,
    string ResourceRequirements, string ArtifactHashSha256, string Provenance);

public sealed record OperatorAICapabilityDescriptor(string CapabilityId, string Kind, bool AcceptsVideoFrames,
    string ProviderId, string Provider, int ProviderState, OperatorAIModelDescriptor[] Models)
{
    public bool Supported => Kind == "ai.person-segmentation" && AcceptsVideoFrames;
    public bool Available => Supported && ProviderState == 1 && Models.Length > 0;
    public string Status => !Supported ? "UNSUPPORTED" : ProviderState == 3 ? "UNAVAILABLE" :
        ProviderState == 2 ? "DEGRADED" : Available ? "AVAILABLE · PRODUCTION UNVERIFIED" : "UNVERIFIED";
    public string Label => Kind + " · " + Status;
    public string Detail => Provider + " · " + string.Join("; ", Models.Select(model =>
        model.ModelVersion + " · " + model.InputContract + " → " + model.OutputContract + " · " + model.Provenance));
}
