// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Media.Contracts;
namespace rtaime.ControlHost;

// Ephemeral Control-lifetime evidence. Never persisted as a second playlist or reused on restart.
public sealed record PreparedRundownMedia(MediaDeckPreparation Preparation, ulong CatalogueRevision,
    string SourceLocation, string RuntimeHostInstanceId);
