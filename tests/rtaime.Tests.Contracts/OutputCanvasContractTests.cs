// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.Text.Json;
using rtaime.Control.Contracts;
namespace rtaime.Tests.Contracts;

public sealed class OutputCanvasContractTests
{
    [Fact]
    public void Strict_canvas_payload_rejects_duplicates_unknown_fields_and_oversize()
    {
        Assert.Throws<InvalidDataException>(() => OutputCanvasJson.Read<OutputCanvasSaveRequest>("{\"version\":\"1.0\",\"Version\":\"2.0\"}"));
        Assert.Throws<JsonException>(() => OutputCanvasJson.Read<OutputCanvasSaveRequest>("{\"unexpected\":true}"));
        Assert.Throws<InvalidDataException>(() => OutputCanvasJson.Read<OutputCanvasSaveRequest>(new string(' ',OutputCanvasContract.MaximumJsonBytes + 1)));
    }

    [Fact]
    public void Canvas_contract_roundtrip_retains_stable_identity_and_numeric_edit_values()
    {
        var configurationId = Guid.NewGuid(); var transactionId = Guid.NewGuid(); var id = Guid.NewGuid();
        var request = new OutputCanvasSaveRequest(OutputCanvasContract.Version,transactionId,configurationId,17,
            [new(OutputCanvasEditKind.AddCanvas,id,Canvas:new(id,"Canvas",1280,720))]);
        var result = OutputCanvasJson.Read<OutputCanvasSaveRequest>(JsonSerializer.Serialize(request,OutputCanvasJson.Options));
        Assert.Equal(configurationId,result.ConfigurationId);
        Assert.Equal(transactionId,result.TransactionId);
        Assert.Equal(17UL,result.ExpectedSavedRevision);
        Assert.Equal(request.Edits[0],Assert.Single(result.Edits));
        Assert.Equal(1,(int)OutputCanvasEditKind.AddCanvas);
        Assert.Equal(8,(int)OutputCanvasEditKind.Unbind);
    }
}
