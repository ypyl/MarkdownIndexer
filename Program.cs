using PageIndex;
using System.Text.Json;

// =========================================================================
// Verification tests for MarkdownIndexer
// =========================================================================

// Simple char/4 heuristic for testing (no NuGet needed)
static int ApproxTokenCount(string text) => text.Length / 4;

// =========================================================================
// 4.1 Core parser: compare output shape against Python reference
// =========================================================================

Console.WriteLine("=== 4.1 Core Parser Output ===");

string md = """
    # My Document

    Intro text here.

    ## Section A

    Content A.

    ### Subsection A.1

    Detailed content A.1.

    ## Section B

    Content B.
    """;

var result = MarkdownIndexer.Index(md, "test-doc", new IndexerOptions
{
    AddNodeId = true,
    AddNodeText = true
});

string json = MarkdownIndexer.ToJson(result);
Console.WriteLine(json);

// Verify basic structure
Assert(result.DocName == "test-doc", "4.1: doc_name");
Assert(result.LineCount > 0, "4.1: line_count");
Assert(result.Structure.Count > 0, "4.1: structure exists");

// Verify node IDs are 4-digit zero-padded
var flat = MarkdownIndexer.FlattenTree(result.Structure);
Assert(flat.All(n => n.NodeId.Length == 4 && int.TryParse(n.NodeId, out _)),
    "4.1: node_id format (0001, 0002, ...)");

// Verify snake_case keys in JSON
Assert(json.Contains("\"doc_name\""), "4.1: snake_case doc_name");
Assert(json.Contains("\"node_id\""), "4.1: snake_case node_id");
Assert(json.Contains("\"line_num\""), "4.1: snake_case line_num");

Console.WriteLine("4.1 PASSED\n");

// =========================================================================
// 4.3 Code-block header exclusion
// =========================================================================

Console.WriteLine("=== 4.3 Code Block Exclusion ===");

string mdWithCode = """
    # Real Header

    ```python
    # This is NOT a header -- it's inside a code block
    ## Also not a header
    print("hello")
    ```

    ## Real Section

    ```
    # Another fake header
    ```

    ### After nested fence
    """;

var result2 = MarkdownIndexer.Index(mdWithCode, "code-test", new IndexerOptions
{
    AddNodeId = true,
    AddNodeText = true
});

var flat2 = MarkdownIndexer.FlattenTree(result2.Structure);
var titles = flat2.Select(n => n.Title).ToList();

Console.WriteLine($"Headers found: {string.Join(", ", titles)}");
Assert(titles.Count == 3, "4.3: exactly 3 headers (not 6)");
Assert(titles.Contains("Real Header"), "4.3: Real Header found");
Assert(titles.Contains("Real Section"), "4.3: Real Section found");
Assert(titles.Contains("After nested fence"), "4.3: After nested fence found");
Assert(!titles.Contains("This is NOT a header"), "4.3: code block header excluded");
Assert(!titles.Contains("Also not a header"), "4.3: code block header excluded");
Assert(!titles.Contains("Another fake header"), "4.3: second code block header excluded");

Console.WriteLine("4.3 PASSED\n");

// =========================================================================
// 4.4 Edge cases
// =========================================================================

Console.WriteLine("=== 4.4 Edge Cases ===");

// Empty document
var empty = MarkdownIndexer.Index("", "empty");
Assert(empty.Structure.Count == 0, "4.4: empty doc has no structure");

// No headers
var noHeaders = MarkdownIndexer.Index("Just some text\nno headers here", "no-headers");
Assert(noHeaders.Structure.Count == 0, "4.4: no headers produces empty tree");

// All H1 level (multiple roots)
string allH1 = """
    # Root 1
    Content 1

    # Root 2
    Content 2

    # Root 3
    Content 3
    """;
var multiRoot = MarkdownIndexer.Index(allH1, "multi-root", new IndexerOptions { AddNodeId = true });
Assert(multiRoot.Structure.Count == 3, "4.4: three root nodes");
Assert(multiRoot.Structure[0].Nodes is null, "4.4: root 1 has no children");
Assert(multiRoot.Structure[1].Nodes is null, "4.4: root 2 has no children");

// Deeply nested (6 levels)
string deep = """
    # L1
    ## L2
    ### L3
    #### L4
    ##### L5
    ###### L6
    """;
var deepResult = MarkdownIndexer.Index(deep, "deep", new IndexerOptions { AddNodeId = true });
var deepFlat = MarkdownIndexer.FlattenTree(deepResult.Structure);
Assert(deepFlat.Count == 6, "4.4: 6 levels preserved");

// Headers with special characters
string specialChars = """
    # Hello **World**!
    ## Section: With Colons & Ampersands
    """;
var specialResult = MarkdownIndexer.Index(specialChars, "special", new IndexerOptions { AddNodeId = true });
var specialFlat = MarkdownIndexer.FlattenTree(specialResult.Structure);
Assert(specialFlat.Any(n => n.Title.Contains("**World**")), "4.4: markdown formatting in title preserved");
Assert(specialFlat.Any(n => n.Title.Contains("Colons")), "4.4: special chars preserved");

Console.WriteLine("4.4 PASSED\n");

// =========================================================================
// 4.2 Thinning
// =========================================================================

Console.WriteLine("=== 4.2 Thinning ===");

string thinMd = """
    # Parent
    Short intro.

    ## Small A
    Just a bit of text.

    ### Tiny Sub
    Minimal.

    ## Big Child
    This child has a lot more content to make it exceed the token threshold.
    Let me add more text here to ensure this section is long enough.
    Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod
    tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim
    veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea
    commodo consequat. Duis aute irure dolor in reprehenderit in voluptate
    velit esse cillum dolore eu fugiat nulla pariatur.
    """;

// Without thinning
var noThin = MarkdownIndexer.Index(thinMd, "no-thin", new IndexerOptions { AddNodeId = true });
var noThinFlat = MarkdownIndexer.FlattenTree(noThin.Structure);
Console.WriteLine($"Without thinning: {noThinFlat.Count} nodes");
Console.WriteLine($"  Titles: {string.Join(", ", noThinFlat.Select(n => n.Title))}");

// With thinning (threshold = 100 tokens ≈ 400 chars)
// "Small A" + "Tiny Sub" combined are ~15 tokens → below 100 → Tiny Sub merged into Small A
// "Big Child" is above 100 → preserved
var thinned = MarkdownIndexer.Index(thinMd, "thinned", new IndexerOptions
{
    AddNodeId = true,
    Thinning = new(Threshold: 100),
    AddNodeText = true
}, ApproxTokenCount);
var thinnedFlat = MarkdownIndexer.FlattenTree(thinned.Structure);
Console.WriteLine($"With thinning (threshold=100): {thinnedFlat.Count} nodes");
Console.WriteLine($"  Titles: {string.Join(", ", thinnedFlat.Select(n => n.Title))}");

// "Tiny Sub" should be absorbed into "Small A"
Assert(thinnedFlat.Count < noThinFlat.Count, "4.2: thinning reduced node count");
Assert(thinnedFlat.Any(n => n.Title == "Parent"), "4.2: Parent still exists");
Assert(thinnedFlat.Any(n => n.Title == "Small A"), "4.2: Small A survived (absorbed Tiny Sub)");
Assert(thinnedFlat.Any(n => n.Title == "Big Child"), "4.2: Big Child survived thinning");
Assert(!thinnedFlat.Any(n => n.Title == "Tiny Sub"), "4.2: Tiny Sub was thinned");

Console.WriteLine("4.2 PASSED\n");

// =========================================================================
// 4.5 JSON output: null fields and empty nodes arrays omitted
// =========================================================================

Console.WriteLine("=== 4.5 JSON Output Shape ===");

string simpleMd = """
    # Only One Header
    Content here.
    """;

var simpleResult = MarkdownIndexer.Index(simpleMd, "simple", new IndexerOptions { AddNodeId = true, AddNodeText = true });
string simpleJson = MarkdownIndexer.ToJson(simpleResult);

// Verify no empty "nodes" array
Assert(!simpleJson.Contains("\"nodes\": []"), "4.5: no empty nodes arrays");
// Verify null fields are absent
Assert(!simpleJson.Contains("\"summary\": null"), "4.5: null summary omitted");
Assert(!simpleJson.Contains("\"prefix_summary\": null"), "4.5: null prefix_summary omitted");
// When AddNodeText is true, text should be present
Assert(simpleJson.Contains("\"text\""), "4.5: text present when AddNodeText=true");

// When AddNodeText is false, text should be absent
var noTextResult = MarkdownIndexer.Index(simpleMd, "no-text", new IndexerOptions { AddNodeId = true, AddNodeText = false });
string noTextJson = MarkdownIndexer.ToJson(noTextResult);
Assert(!noTextJson.Contains("\"text\""), "4.5: text absent when AddNodeText=false");

Console.WriteLine("4.5 PASSED\n");

// =========================================================================
// 4.6 Enrichment delegates called with correct prompts
// =========================================================================

Console.WriteLine("=== 4.6 Enrichment ===");

string enrichMd = """
    # Test Document

    This document has enough content to test summarization.
    It needs more than the default 200-token threshold, so let's add
    a substantial amount of text here. Lorem ipsum dolor sit amet,
    consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut
    labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud
    exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.

    ## Section One

    Another substantial section with enough content. Duis aute irure dolor
    in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla
    pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa
    qui officia deserunt mollit anim id est laborum.
    """;

List<string> llmCalls = [];
Task<string> trackingLlm(string prompt, CancellationToken ct)
{
    llmCalls.Add(prompt);
    return Task.FromResult($"Summary of: {prompt[..Math.Min(50, prompt.Length)]}...");
}

var enrichResult = await MarkdownIndexer.IndexWithEnrichmentAsync(
    enrichMd,
    "enrich-test",
    ApproxTokenCount,
    trackingLlm,
    new IndexerOptions { AddNodeId = true, Thinning = null },
    new EnrichmentOptions
    {
        SummaryTokenThreshold = 50, // low threshold to trigger LLM calls
        AddDocDescription = true,
        KeepText = false
    });

// Verify LLM was called
Assert(llmCalls.Count >= 2, $"4.6: at least 2 LLM calls (got {llmCalls.Count})");
// First calls should contain the prompt template instruction
Assert(llmCalls[0].Contains("You are given a part of a document"),
    "4.6: summary prompt template used");

// Verify summaries assigned
var enrichedFlat = MarkdownIndexer.FlattenTree(enrichResult.Structure);
int withSummary = enrichedFlat.Count(n => n.Summary is not null || n.PrefixSummary is not null);
Assert(withSummary >= 2, $"4.6: summaries assigned ({withSummary} nodes)");

// Verify doc description was generated
Assert(enrichResult.DocDescription is not null, "4.6: doc description generated");
Assert(enrichResult.DocDescription!.Length > 0, "4.6: doc description non-empty");

// Verify text was stripped (KeepText = false)
Assert(enrichedFlat.All(n => n.Text is null), "4.6: text stripped after enrichment");

Console.WriteLine($"LLM calls made: {llmCalls.Count}");
Console.WriteLine("4.6 PASSED\n");

// =========================================================================
// 4.7 Concurrency throttle
// =========================================================================

Console.WriteLine("=== 4.7 Concurrency Throttle ===");

// Create a document with many sections to test throttling
var sb = new System.Text.StringBuilder();
sb.AppendLine("# Many Sections Doc\n");
for (int i = 1; i <= 10; i++)
{
    sb.AppendLine($"## Section {i}");
    sb.AppendLine($"Content for section {i}. " + new string('x', 200));
    sb.AppendLine();
}

int concurrentCalls = 0;
int maxConcurrentObserved = 0;
var lockObj = new object();

Task<string> throttledLlm(string prompt, CancellationToken ct)
{
    int current;
    lock (lockObj)
    {
        current = Interlocked.Increment(ref concurrentCalls);
        maxConcurrentObserved = Math.Max(maxConcurrentObserved, current);
    }

    // Simulate some async work
    return Task.Delay(50, ct).ContinueWith(_ =>
    {
        Interlocked.Decrement(ref concurrentCalls);
        return $"Summary {current}";
    });
}

await MarkdownIndexer.IndexWithEnrichmentAsync(
    sb.ToString(),
    "throttle-test",
    text => text.Length / 4,
    throttledLlm,
    new IndexerOptions { AddNodeId = true },
    new EnrichmentOptions
    {
        SummaryTokenThreshold = 50,
        MaxConcurrency = 2
    });

Assert(maxConcurrentObserved <= 2,
    $"4.7: max concurrency {maxConcurrentObserved} <= 2 (throttled)");
Console.WriteLine($"Max concurrent LLM calls observed: {maxConcurrentObserved}");
Console.WriteLine("4.7 PASSED\n");

// =========================================================================
// Summary
// =========================================================================

Console.WriteLine("========================================");
Console.WriteLine("  ALL VERIFICATION TESTS PASSED");
Console.WriteLine("========================================");

// =========================================================================
// Helpers
// =========================================================================

static void Assert(bool condition, string label)
{
    if (!condition)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL: {label}");
        Console.ResetColor();
        Environment.Exit(1);
    }
}
