using System.Collections.Concurrent;
using System.Text.Json;

namespace PageIndex;

public static partial class MarkdownIndexer
{
    private const string SummaryPromptTemplate = """
        You are given a part of a document, your task is to generate a description 
        of the partial document about what are main points covered in the partial document.

        Partial Document Text: {0}

        Directly return the description, do not include any other text.
        """;

    private const string DocDescriptionPromptTemplate = """
        Your are an expert in generating descriptions for a document.
        You are given a structure of a document. Your task is to generate a 
        one-sentence description for the document, which makes it easy to 
        distinguish the document from other documents.

        Document Structure: {0}

        Directly return the description, do not include any other text.
        """;

    private static List<TreeNode> BuildCleanStructureForDescription(List<TreeNode> roots)
    {
        var result = new List<TreeNode>();
        foreach (var node in roots)
        {
            var clean = new TreeNode
            {
                Title = node.Title,
                NodeId = node.NodeId,
                LineNum = node.LineNum,
                Summary = node.Summary,
                PrefixSummary = node.PrefixSummary,
                Nodes = node.Nodes is not null
                    ? BuildCleanStructureForDescription(node.Nodes)
                    : null
            };
            result.Add(clean);
        }
        return result;
    }

    private static async Task GenerateSummariesAsync(
        List<TreeNode> roots,
        Func<string, int> tokenCounter,
        Func<string, CancellationToken, Task<string>> summarizeAsync,
        EnrichmentOptions options,
        CancellationToken ct)
    {
        var allNodes = FlattenTree(roots);

        var llmTasks = new List<(TreeNode Node, int Index)>();
        for (int i = 0; i < allNodes.Count; i++)
        {
            var node = allNodes[i];
            string? text = node.Text;
            if (string.IsNullOrEmpty(text))
                continue;

            int tokens = tokenCounter(text);
            if (tokens < options.SummaryTokenThreshold)
            {
                AssignSummary(node, text);
            }
            else
            {
                llmTasks.Add((node, i));
            }
        }

        if (llmTasks.Count > 0)
        {
            var results = new ConcurrentDictionary<int, string>();
            int maxConcurrency = options.MaxConcurrency > 0
                ? options.MaxConcurrency
                : llmTasks.Count;

            var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

            var tasks = llmTasks.Select(async item =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    string prompt = string.Format(SummaryPromptTemplate, item.Node.Text);
                    string summary = await summarizeAsync(prompt, ct);
                    results[item.Index] = summary;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            foreach (var item in llmTasks)
            {
                if (results.TryGetValue(item.Index, out string? summary))
                    AssignSummary(item.Node, summary);
            }
        }
    }

    private static void AssignSummary(TreeNode node, string summary)
    {
        if (node.Nodes is null or { Count: 0 })
            node.Summary = summary;
        else
            node.PrefixSummary = summary;
    }

    private static async Task<string> GenerateDocDescriptionAsync(
        List<TreeNode> roots,
        Func<string, CancellationToken, Task<string>> describeAsync,
        CancellationToken ct)
    {
        var clean = BuildCleanStructureForDescription(roots);
        string structureJson = JsonSerializer.Serialize(clean, JsonOptions);
        string prompt = string.Format(DocDescriptionPromptTemplate, structureJson);
        return await describeAsync(prompt, ct);
    }

    public static async Task<IndexResult> EnrichAsync(
        IndexResult result,
        Func<string, int> tokenCounter,
        Func<string, CancellationToken, Task<string>> summarizeAsync,
        EnrichmentOptions? options = null,
        Func<string, CancellationToken, Task<string>>? describeAsync = null,
        CancellationToken ct = default)
    {
        options ??= new EnrichmentOptions();

        await GenerateSummariesAsync(result.Structure, tokenCounter, summarizeAsync, options, ct);

        if (options.AddDocDescription)
        {
            var descDelegate = describeAsync ?? summarizeAsync;
            result.DocDescription = await GenerateDocDescriptionAsync(
                result.Structure, descDelegate, ct);
        }

        if (!options.KeepText)
            StripText(result.Structure);

        return result;
    }

    public static async Task<IndexResult> IndexWithEnrichmentAsync(
        string markdownContent,
        string docName,
        Func<string, int> tokenCounter,
        Func<string, CancellationToken, Task<string>> summarizeAsync,
        IndexerOptions? parseOptions = null,
        EnrichmentOptions? enrichOptions = null,
        Func<string, CancellationToken, Task<string>>? describeAsync = null,
        CancellationToken ct = default)
    {
        parseOptions ??= new IndexerOptions();
        parseOptions = new IndexerOptions
        {
            AddNodeId = parseOptions.AddNodeId,
            Thinning = parseOptions.Thinning,
            AddNodeText = true
        };

        var result = Index(markdownContent, docName, parseOptions, tokenCounter);
        return await EnrichAsync(result, tokenCounter, summarizeAsync, enrichOptions, describeAsync, ct);
    }
}
