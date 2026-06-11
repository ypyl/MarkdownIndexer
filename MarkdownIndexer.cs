using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PageIndex;

public sealed record IndexerOptions
{
    public bool AddNodeId { get; init; } = true;
    public ThinningConfig? Thinning { get; init; }
    public bool AddNodeText { get; init; } = false;
    public sealed record ThinningConfig(int Threshold = 5000);
}

public sealed record EnrichmentOptions
{
    public int SummaryTokenThreshold { get; init; } = 200;
    public int MaxConcurrency { get; init; } = 0;
    public bool AddDocDescription { get; init; } = false;
    public bool KeepText { get; init; } = false;
}

public sealed record TreeNode
{
    [JsonPropertyName("title")]
    [JsonPropertyOrder(0)]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("node_id")]
    [JsonPropertyOrder(1)]
    public string NodeId { get; set; } = string.Empty;

    [JsonPropertyName("line_num")]
    [JsonPropertyOrder(2)]
    public int LineNum { get; init; }

    [JsonPropertyName("summary")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("prefix_summary")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrefixSummary { get; set; }

    [JsonPropertyName("text")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("nodes")]
    [JsonPropertyOrder(6)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TreeNode>? Nodes { get; set; }
}

public sealed record IndexResult
{
    [JsonPropertyName("doc_name")]
    public string DocName { get; init; } = string.Empty;

    [JsonPropertyName("line_count")]
    public int LineCount { get; init; }

    [JsonPropertyName("structure")]
    public List<TreeNode> Structure { get; init; } = [];

    [JsonPropertyName("doc_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DocDescription { get; set; }
}

internal sealed class FlatNode
{
    public string Title { get; set; } = string.Empty;
    public int LineNum { get; set; }
    public int Level { get; set; }
    public string Text { get; set; } = string.Empty;
    public int TextTokenCount { get; set; }
}

public static partial class MarkdownIndexer
{
    private static List<FlatNode> ExtractHeaders(string markdown)
    {
        var headers = new List<FlatNode>();
        var lines = markdown.Split('\n');
        bool inCodeBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            if (line.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (line.Length == 0 || inCodeBlock)
                continue;

            var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (match.Success)
            {
                headers.Add(new FlatNode
                {
                    Title = match.Groups[2].Value.Trim(),
                    LineNum = i + 1,
                    Level = match.Groups[1].Length
                });
            }
        }

        return headers;
    }

    private static void ExtractText(List<FlatNode> nodes, string[] lines)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            int start = nodes[i].LineNum - 1;
            int end = (i + 1 < nodes.Count) ? nodes[i + 1].LineNum - 1 : lines.Length;
            nodes[i].Text = string.Join("\n", lines[start..end]).TrimEnd();
        }
    }

    private static void ThinTree(
        List<FlatNode> nodes,
        int threshold,
        Func<string, int> tokenCounter)
    {
        ComputeTokenCounts(nodes, tokenCounter);

        var toRemove = new HashSet<int>();

        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (toRemove.Contains(i))
                continue;

            if (nodes[i].TextTokenCount >= threshold)
                continue;

            var children = FindChildren(i, nodes);
            if (children.Count == 0)
                continue;

            var sb = new StringBuilder(nodes[i].Text);
            foreach (int childIdx in children)
            {
                if (toRemove.Contains(childIdx))
                    continue;

                if (sb.Length > 0 && !sb.ToString().EndsWith('\n'))
                    sb.Append("\n\n");
                sb.Append(nodes[childIdx].Text);
                toRemove.Add(childIdx);
            }
            nodes[i].Text = sb.ToString().TrimEnd();
            nodes[i].TextTokenCount = tokenCounter(nodes[i].Text);
        }

        foreach (int idx in toRemove.OrderByDescending(x => x))
            nodes.RemoveAt(idx);
    }

    private static void ComputeTokenCounts(List<FlatNode> nodes, Func<string, int> tokenCounter)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            var children = FindChildren(i, nodes);
            var sb = new StringBuilder(nodes[i].Text);
            foreach (int childIdx in children)
            {
                if (sb.Length > 0)
                    sb.Append('\n');
                sb.Append(nodes[childIdx].Text);
            }
            nodes[i].TextTokenCount = tokenCounter(sb.ToString());
        }
    }

    private static List<int> FindChildren(int parentIndex, List<FlatNode> nodes)
    {
        var children = new List<int>();
        int parentLevel = nodes[parentIndex].Level;

        for (int i = parentIndex + 1; i < nodes.Count; i++)
        {
            if (nodes[i].Level <= parentLevel)
                break;
            children.Add(i);
        }

        return children;
    }

    private static List<TreeNode> BuildTree(List<FlatNode> flatNodes)
    {
        if (flatNodes.Count == 0)
            return [];

        var stack = new Stack<(TreeNode Node, int Level)>();
        var roots = new List<TreeNode>();

        foreach (var fn in flatNodes)
        {
            var treeNode = new TreeNode
            {
                Title = fn.Title,
                LineNum = fn.LineNum,
                Text = fn.Text,
                NodeId = string.Empty
            };

            while (stack.Count > 0 && stack.Peek().Level >= fn.Level)
                stack.Pop();

            if (stack.Count == 0)
            {
                roots.Add(treeNode);
            }
            else
            {
                var parent = stack.Peek().Node;
                parent.Nodes ??= [];
                parent.Nodes.Add(treeNode);
            }

            stack.Push((treeNode, fn.Level));
        }

        return roots;
    }

    private static void AssignNodeIds(List<TreeNode> roots)
    {
        int counter = 0;
        AssignIdsRecursive(roots, ref counter);
    }

    private static void AssignIdsRecursive(List<TreeNode> nodes, ref int counter)
    {
        foreach (var node in nodes)
        {
            counter++;
            node.NodeId = counter.ToString("D4");
            if (node.Nodes is { Count: > 0 })
                AssignIdsRecursive(node.Nodes, ref counter);
        }
    }

    private static void CleanEmptyNodes(List<TreeNode> roots)
    {
        foreach (var node in roots)
        {
            if (node.Nodes is { Count: 0 })
                node.Nodes = null;
            if (node.Nodes is not null)
                CleanEmptyNodes(node.Nodes);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ToJson(IndexResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    public static IndexResult Index(
        string markdownContent,
        string docName,
        IndexerOptions? options = null,
        Func<string, int>? tokenCounter = null)
    {
        options ??= new IndexerOptions();

        int lineCount = markdownContent.Count(c => c == '\n') + 1;

        var nodes = ExtractHeaders(markdownContent);

        var lines = markdownContent.Split('\n');
        ExtractText(nodes, lines);

        if (options.Thinning is { } thinning && tokenCounter is not null)
            ThinTree(nodes, thinning.Threshold, tokenCounter);

        var tree = BuildTree(nodes);

        if (options.AddNodeId)
            AssignNodeIds(tree);

        CleanEmptyNodes(tree);

        if (!options.AddNodeText)
            StripText(tree);

        return new IndexResult
        {
            DocName = docName,
            LineCount = lineCount,
            Structure = tree
        };
    }

    internal static void StripText(List<TreeNode> roots)
    {
        foreach (var node in roots)
        {
            node.Text = null;
            if (node.Nodes is not null)
                StripText(node.Nodes);
        }
    }

    internal static List<TreeNode> FlattenTree(List<TreeNode> roots)
    {
        var result = new List<TreeNode>();
        FlattenRecursive(roots, result);
        return result;
    }

    private static void FlattenRecursive(List<TreeNode> nodes, List<TreeNode> accumulator)
    {
        foreach (var node in nodes)
        {
            accumulator.Add(node);
            if (node.Nodes is not null)
                FlattenRecursive(node.Nodes, accumulator);
        }
    }
}
