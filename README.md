# MarkdownIndexer

Vendorable C# markdown indexing — parse `.md` into a structured JSON tree with optional LLM summarization. **Zero NuGet dependencies** in the vendored files.

Designed to match the output of [PageIndex](https://github.com/ypyl/pageindex)'s markdown pipeline.

## Architecture

Two files you drop into any .NET project:

| File | Responsibility | External deps |
|------|---------------|---------------|
| `MarkdownIndexer.cs` | Parse markdown → tree + JSON | None (BCL only) |
| `MarkdownIndexer.Enrichment.cs` | LLM summaries + doc description | None (BCL only) |

All external concerns are injected as delegates:

- Token counting → `Func<string, int>`
- LLM calls → `Func<string, CancellationToken, Task<string>>`

You wire these up from whatever libraries your project already uses.

## Pipeline

```
MARKDOWN STRING
      │
      ▼
┌─────────────────────────────────────────┐
│ EXTRACT HEADERS                         │
│   Regex ^(#{1,6})\s+(.+)$,              │
│   skips ``` code blocks                 │
│   → [{title, line_num, level}, ...]    │
└──────────────────┬──────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│ EXTRACT TEXT PER SECTION                │
│   Slice from header line to next        │
│   header (or EOF)                       │
│   → [{title, line_num, level, text}]   │
└──────────────────┬──────────────────────┘
                   │
          ┌────────┴────────┐
          │ if thinning     │
          ▼                 │
┌──────────────────┐        │
│ THIN TREE        │        │
│ Bottom-up token  │        │
│ count. Merge     │        │
│ small subtrees   │        │
│ into parents.    │        │
└────────┬─────────┘        │
         │                  │
         └────────┬─────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│ STACK-BASED TREE BUILDING               │
│   Push/pop by header level.             │
│   Children of nearest lower-level       │
│   ancestor.                             │
└──────────────────┬──────────────────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│ DEPTH-FIRST NODE IDS + CLEANUP          │
│   "0001", "0002", ...                   │
│   Remove empty nodes arrays             │
└──────────────────┬──────────────────────┘
                   │
          ┌────────┴────────┐
          │ if enrichment   │
          ▼                 │
┌──────────────────┐        │
│ LLM SUMMARIZATION│        │
│ Per node (parallel│       │
│ + throttled).    │        │
│ Leaf → summary   │        │
│ Parent → prefix_ │        │
│          summary │        │
│                  │        │
│ DOC DESCRIPTION  │        │
│ One-sentence     │        │
│ (single LLM call)│       │
│                  │        │
│ STRIP TEXT       │        │
│ (optional)       │        │
└────────┬─────────┘        │
         │                  │
         └────────┬─────────┘
                   │
                   ▼
┌─────────────────────────────────────────┐
│ JSON OUTPUT                             │
│   { doc_name, line_count,               │
│     structure: [{title, node_id,         │
│       line_num, summary?,              │
│       prefix_summary?, text?,           │
│       nodes?}, ...] }                   │
└─────────────────────────────────────────┘
```

## Quick Start

Add the two `.cs` files to your project. Install your choice of tokenizer and AI packages:

```bash
dotnet add package Microsoft.ML.Tokenizers
dotnet add package Microsoft.Extensions.AI.OpenAI
```

### Parse only (no LLM)

```csharp
using PageIndex;
using Microsoft.ML.Tokenizers;

var md = File.ReadAllText("doc.md");
var tokenizer = TiktokenTokenizer.CreateForModel("gpt-4o");

var result = MarkdownIndexer.Index(md, "doc", new IndexerOptions
{
    AddNodeId = true,
    AddNodeText = true,
    Thinning = new(Threshold: 5000)       // optional
}, text => tokenizer.CountTokens(text));

string json = MarkdownIndexer.ToJson(result);
```

### Parse + LLM enrichment

```csharp
var chatClient = new OpenAIClient(apiKey)
    .AsChatClient("gpt-4o-mini");

Func<string, CancellationToken, Task<string>> llm =
    async (prompt, ct) =>
    {
        var response = await chatClient.GetResponseAsync(prompt, cancellationToken: ct);
        return response.Text;
    };

var result = await MarkdownIndexer.IndexWithEnrichmentAsync(
    md, "doc",
    text => tokenizer.CountTokens(text),
    llm,
    enrichOptions: new EnrichmentOptions
    {
        SummaryTokenThreshold = 200,
        MaxConcurrency = 5,
        AddDocDescription = true
    });

string json = MarkdownIndexer.ToJson(result);
```

### Using any LLM provider

The delegate pattern works with any client library:

```csharp
// Anthropic
Func<string, CancellationToken, Task<string>> llm = async (prompt, ct) =>
{
    var msg = await anthropic.Messages.GetResponseAsync(new()
    {
        Model = "claude-sonnet-4-20250514",
        Messages = [new() { Content = prompt }]
    }, ct);
    return msg.Content.First().Text;
};

// Ollama (local)
Func<string, CancellationToken, Task<string>> llm = async (prompt, ct) =>
{
    var response = await httpClient.PostAsJsonAsync(
        "http://localhost:11434/api/generate",
        new { model = "llama3", prompt, stream = false }, ct);
    var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    return json.GetProperty("response").GetString()!;
};
```

## Output Shape

```json
{
  "doc_name": "my-document",
  "line_count": 42,
  "doc_description": "A document covering markdown parsing techniques.",
  "structure": [
    {
      "title": "My Document",
      "node_id": "0001",
      "line_num": 1,
      "prefix_summary": "The document introduces...",
      "nodes": [
        {
          "title": "Section A",
          "node_id": "0002",
          "line_num": 5,
          "summary": "Section A details the core algorithm."
        }
      ]
    }
  ]
}
```

Key conventions (matching Python PageIndex):

- `summary` — leaf nodes (no children)
- `prefix_summary` — parent nodes (signpost before descending into children)
- `text` — excluded when `AddNodeText` is false or `KeepText` is false (after summarization)
- Empty `nodes` arrays are omitted from JSON

## Options

### IndexerOptions

| Option | Default | |
|--------|---------|---|
| `AddNodeId` | `true` | Assign depth-first "0001", "0002", ... |
| `Thinning` | `null` | `new(Threshold: 5000)` to merge small subtrees |
| `AddNodeText` | `false` | Include `text` in output |

### EnrichmentOptions

| Option | Default | |
|--------|---------|---|
| `SummaryTokenThreshold` | `200` | Skip LLM for sections below this token count |
| `MaxConcurrency` | `0` (unlimited) | Max parallel LLM calls |
| `AddDocDescription` | `false` | Generate one-sentence document description |
| `KeepText` | `false` | Retain text after summarization |

## Customization

Both files are designed for copy-paste vendoring. Edit prompts directly:

```csharp
// In MarkdownIndexer.Enrichment.cs
private const string SummaryPromptTemplate = """
    You are given...
    Partial Document Text: {0}
    Directly return the description...
    """;
```

Or customize the pipeline by calling individual methods (`GenerateSummariesAsync`, `GenerateDocDescriptionAsync`, `StripText`) instead of using the convenience orchestrators.
