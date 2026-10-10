// Copyright (c) Dave Beusing <david.beusing@gmail.com>.

using rtaime.Recording;

namespace rtaime.Tests.Unit;

public sealed class MxfVerifiedFilePublisherTests
{
    [Fact]
    public void Independent_probe_must_succeed_before_partial_is_promoted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rtaime-mxf-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var partial = Path.Combine(folder, "recording.partial.mxf");
            var final = Path.Combine(folder, "recording.mxf");
            File.WriteAllBytes(partial, [1, 2, 3]);
            Assert.Throws<InvalidDataException>(() =>
                MxfVerifiedFilePublisher.Publish(partial, final, _ => throw new InvalidDataException("Invalid file.")));
            Assert.True(File.Exists(partial));
            Assert.False(File.Exists(final));

            MxfVerifiedFilePublisher.Publish(partial, final, file => Assert.Equal(3, File.ReadAllBytes(file).Length));
            Assert.False(File.Exists(partial));
            Assert.True(File.Exists(final));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Rejects_mismatched_names_and_overwriting_an_existing_final()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rtaime-mxf-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var partial = Path.Combine(folder, "take.partial.mxf");
            var final = Path.Combine(folder, "take.mxf");
            File.WriteAllBytes(partial, [7]);
            File.WriteAllBytes(final, [8]);
            Assert.Throws<InvalidOperationException>(() =>
                MxfVerifiedFilePublisher.Publish(partial, Path.Combine(folder, "another.mxf"), _ => { }));
            Assert.Throws<IOException>(() => MxfVerifiedFilePublisher.Publish(partial, final, _ => { }));
            Assert.Equal((byte)8, Assert.Single(File.ReadAllBytes(final)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
