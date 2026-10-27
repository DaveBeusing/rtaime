// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using rtaime.Client;
using rtaime.Operator;
using Xunit;

namespace rtaime.Tests.Operator;

public sealed class CompositingGraphAuthoringTests
{
    private static CompositingGraphNodeProjection Node(string id, params CompositingGraphPortProjection[] ports) =>
        new(id, CompositingGraphNodeKind.Processing, id, "static", "CONFIRMED",
            CompositingGraphHealth.Normal, ports);

    private static CompositingGraphProjection ValidGraph()
    {
        var nodes = new[]
        {
            Node("source", new("out", "Video", CompositingGraphPortDirection.Output),
                new("in", "Feedback", CompositingGraphPortDirection.Input)),
            Node("grade", new("in", "Video", CompositingGraphPortDirection.Input),
                new("out", "Processed", CompositingGraphPortDirection.Output)),
            Node("program", new("in", "Video", CompositingGraphPortDirection.Input),
                new("out", "Video", CompositingGraphPortDirection.Output))
        };
        var connections = new[]
        {
            new CompositingGraphConnectionProjection("s-g", "source", "out", "grade", "in"),
            new CompositingGraphConnectionProjection("g-p", "grade", "out", "program", "in")
        };
        return new(nodes, connections);
    }

    [Fact]
    public void Confirmed_acyclic_paths_pass_but_no_mutable_rewire_capability_is_inferred()
    {
        var graph = ValidGraph();
        var result = CompositingGraphTopologyValidator.Validate(graph);
        Assert.True(result.IsValid);
        Assert.Contains("READ ONLY", result.Detail);
        Assert.All(graph.Nodes, node => Assert.False(node.CanRewire));
    }

    [Fact]
    public void Missing_dangling_wrong_direction_duplicate_and_cyclic_edges_fail_closed()
    {
        var valid = ValidGraph();
        var baseEdges = valid.Connections.ToArray();
        var broken = new[]
        {
            new CompositingGraphProjection(valid.Nodes, [new("dangling", "source", "out", "missing", "in")]),
            new CompositingGraphProjection(valid.Nodes, [new("wrong", "source", "in", "grade", "in")]),
            new CompositingGraphProjection(valid.Nodes, [baseEdges[0], baseEdges[0]]),
            new CompositingGraphProjection(valid.Nodes, [new("loop", "grade", "out", "grade", "in")]),
            new CompositingGraphProjection(valid.Nodes, [..baseEdges,
                new("back-edge", "program", "out", "source", "in")]),
            new CompositingGraphProjection([valid.Nodes[0], valid.Nodes[0]], []),
            new CompositingGraphProjection([Node("broken", new("out", "A", CompositingGraphPortDirection.Output),
                new("out", "B", CompositingGraphPortDirection.Output))], [])
        };
        Assert.All(broken, graph =>
        {
            var result = CompositingGraphTopologyValidator.Validate(graph);
            Assert.False(result.IsValid);
            Assert.Contains("INVALID", result.Detail);
        });
    }
}
