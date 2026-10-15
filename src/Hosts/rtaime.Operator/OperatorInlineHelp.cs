// Copyright (c) Dave Beusing <david.beusing@gmail.com>.
using System.IO;
using System.Reflection;
using System.Text;

namespace rtaime.Operator;

public static class OperatorInlineHelp
{
    private static readonly Lazy<IReadOnlyList<OperatorHelpTopic>> Topics = new(ReadTopics);
    public static IReadOnlyList<OperatorHelpTopic> RepositoryTopics => Topics.Value;
    private static IReadOnlyList<OperatorHelpTopic> ReadTopics()
    {
        var topics = new List<OperatorHelpTopic>();
        var assembly = typeof(OperatorInlineHelp).Assembly;
        foreach (var name in new[] { "OperatorCommandsAndShortcuts", "MediaLibraryAssetBrowser", "OperatorWorkspaces",
            "DurableShowProjectState", "ShowPreflightAndActivity", "WindowsProductionLifecycle" })
        {
            using var stream = assembly.GetManifestResourceStream("OperatorHelp." + name + ".md");
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var title = name; var body = new StringBuilder(); var index = 0;
            void Add()
            {
                if (body.Length == 0 || topics.Count >= 120) return;
                topics.Add(new(name + ".md#" + index++, title, body.ToString()));
                body.Clear();
            }
            var started = false;
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("#", StringComparison.Ordinal))
                { Add(); title = line.TrimStart('#', ' ').Trim(); started = true; }
                else if (started && body.Length < 8192)
                    body.AppendLine(line[..Math.Min(line.Length, 8192 - body.Length)]);
            }
            Add();
        }
        return topics.AsReadOnly();
    }
}
