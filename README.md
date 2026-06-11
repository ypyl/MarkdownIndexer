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

## When to Use This

Tree-indexing is reasoning-based retrieval — the LLM reads a structured table of contents and navigates to the right sections, like a human expert scanning a document.

### 🟢 Strong fit

| Document type | Why tree-index beats chunking | Typical size |
|--------------|------------------------------|--------------|
| **Documentation sites** (mdBook, Docusaurus) | Already have `#`/`##` hierarchy; chunks destroy cross-section context | 50–500 sections |
| **Technical specs / RFCs** | Requirements cross-reference each other; need section-level context | 20–200 pages |
| **Internal wikis / knowledge bases** | Naturally hierarchical; vector search returns similar-but-wrong pages | 100–1000+ pages |
| **Research notes / lab notebooks** | Methods → results → conclusions form a reasoning chain | 5–50 pages |
| **Regulatory filings, financial reports** (PDF→MD converted) | Quarterly data lives in specific sections; chunking mixes Q1 and Q3 | 100–300 pages |
| **Legal contracts, policy documents** | Meaning depends on which section/clause; definitions matter | 20–200 pages |

### 🟡 Moderate fit

API docs with clear hierarchy, blog archives, textbooks — benefits from structure but vector search may be sufficient for simple factoid queries.

### 🔴 Poor fit

- FAQ / Q&A collections — flat by nature, vector search is faster
- Chat logs, support tickets — no hierarchy
- Product reviews, social media — each item is independent
- Documents shorter than ~5K tokens — just feed to the LLM directly

### Size sweet spot

```
┌────────────┐   ┌───────────────┐   ┌─────────────────────────┐
│ < 5K tokens │   │ 5K – 100K     │   │ 100K+ tokens             │
├─────────────┤   ├───────────────┤   ├─────────────────────────┤
│ No RAG      │   │ Tree indexing  │   │ Tree indexing is          │
│ needed —    │   │ SHINES         │   │ THE ONLY option           │
│ just feed    │   │                │   │                            │
│ to the LLM   │   │ Vector RAG     │   │ Vector RAG fails: chunks   │
│             │   │ also works     │   │ lose cross-section context │
└────────────┘   └───────────────┘   └─────────────────────────┘
```

### vs. Vector RAG

| | Vector RAG | Tree-Index RAG |
|---|---|---|
| **How** | Chunk → embed → similarity search | Parse → build TOC → LLM navigates structure |
| **Accuracy** | ~30-50% on complex docs (FinanceBench) | ~98.7% (PageIndex on FinanceBench) |
| **Cost** | Cheap (no extra LLM calls) | More expensive (LLM reads summaries, navigates) |
| **Latency** | Fast (milliseconds) | Slower (sequential LLM reasoning steps) |
| **Traceability** | "Which chunks were similar" | "Reasoning path through the document tree" |
| **Best for** | Factoid queries, conceptual similarity | Multi-hop reasoning, context-dependent answers |

Core insight: **similarity ≠ relevance**. A paragraph can be semantically similar to a query but completely wrong for the answer. Tree-indexing trades speed for precision — worth it when getting the right answer is non-negotiable.

## How Retrieval Works

This library builds the **index**. The retrieval is a separate concern — it's how a downstream LLM agent uses the tree to answer questions.

### The key design: `prefix_summary` vs `summary`

```
Root (prefix_summary: "FY2024 report covering revenue, risk, guidance")
├── Section 3 (prefix_summary: "Revenue breakdown by segment")     ← signpost
│   ├── 3.1 (summary: "Enterprise $2.1B, +15% YoY")               ← leaf
│   └── 3.2 (summary: "Consumer $1.8B, flat")                     ← leaf
├── Section 4 (prefix_summary: "Risk factors and forward guidance") ← signpost
│   └── ...
└── Section 5 (summary: "Conclusion: strong year, cautious outlook") ← leaf
```

`prefix_summary` acts like a **signpost** — "this branch is about X, descend or skip?"  
`summary` acts like a **destination** — "this leaf contains Y, grab it or pass?"

The LLM reads the tree and decides which nodes to fetch text for.

### Strategy 1: One-shot tree navigation

Feed the entire tree (without text) to the LLM. The tree is compact — just titles + summaries — even a 300-section document is ~5-15K tokens. The LLM selects relevant `node_id`s in one pass.

```
┌──────────────┐     ┌───────────────────┐     ┌──────────────────┐
│ Tree (no     │────▶│ LLM reads all     │────▶│ Fetch text for   │
│ text, just   │     │ summaries, picks  │     │ selected nodes   │
│ summaries)   │     │ relevant node IDs │     │ → generate answer│
└──────────────┘     └───────────────────┘     └──────────────────┘
```

### Strategy 2: Agentic iterative navigation

An LLM agent with three tools navigates step by step, never loading the whole tree or document:

```
get_document_structure()  →  "I see 'Financial Results' section. Let me check it."
                           ↓
get_page_content("15-18") →  "Q3 revenue $4.2B. Need MD&A for Q2 comparison."
                           ↓
get_page_content("8-10")  →  "Q2 was $3.8B. Q3 grew 10.5%." → answer
```

This handles multi-hop questions ("compare Q3 to Q2") where the answer spans multiple sections.

### Complete retrieval example

```csharp
// ── Build phase (offline) ──
// Generate tree WITH summaries but WITHOUT text (compact for context)
var tocTree = MarkdownIndexer.Index(md, "report", new IndexerOptions
{
    AddNodeId = true,
    AddNodeText = false   // text would bloat the context
});

// Generate summaries (or use cached)
await MarkdownIndexer.EnrichAsync(tocTree, tokenCounter, llm, new EnrichmentOptions
{
    SummaryTokenThreshold = 200,
    MaxConcurrency = 5
});
string tocJson = MarkdownIndexer.ToJson(tocTree);

// ── Query phase (online) ──
string query = "What was Q3 enterprise revenue?";

// Step 1: LLM reads TOC, selects relevant nodes
string selectionPrompt = $"""
    Question: {query}
    Document tree structure:
    {tocJson}

    Return JSON with node_ids of relevant sections:
    {{"node_ids": ["0003", "0007"]}}
    """;

string selection = await chatClient.GetResponseAsync(selectionPrompt);
var ids = JsonSerializer.Deserialize<SelectionResult>(selection).NodeIds;

// Step 2: Fetch full text only for selected nodes
var fullTree = MarkdownIndexer.Index(md, "report", new IndexerOptions
{
    AddNodeId = true,
    AddNodeText = true   // now with text
});
var allNodes = MarkdownIndexer.FlattenTree(fullTree.Structure);
var context = string.Join("\n\n",
    allNodes.Where(n => ids.Contains(n.NodeId)).Select(n => n.Text));

// Step 3: Generate answer
string answerPrompt = $"Context:\n{context}\n\nQuestion: {query}";
string answer = await chatClient.GetResponseAsync(answerPrompt);
```

> **Tip:** Cache the tree-with-summaries. Regenerate only when the source document changes. Summaries are the expensive part (LLM calls); the parse itself is deterministic and fast.

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
