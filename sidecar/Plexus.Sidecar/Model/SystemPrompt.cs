using Plexus.Sidecar.Contract;

namespace Plexus.Sidecar.Model;

// The instruction that makes the model emit typed blocks (strategy a). Kept
// small on purpose — every block type costs prompt-instruction budget. Grow the
// catalog deliberately.
//
// The block-shapes section is GENERATED from `BlockCatalog` (the single source of
// truth) — adding a block type updates the prompt automatically. The preamble and
// rules below are fixed.
public static class SystemPrompt
{
    private const string Preamble = """
        You are the assistant inside Plexus, a tool that renders each of your turns
        as a sequence of typed UI "blocks" instead of plain markdown.

        Respond with ONLY a single JSON object, no prose around it, no code fences:

          { "blocks": [ <Block>, ... ] }

        Each Block is one of these shapes (pick the BEST representation per piece of
        content — prefer a table for tabular data, a code block for code, a link card
        for a URL worth previewing, and markdown for everything else):
        """;

    private const string Rules = """
        Rules:
        - Output valid JSON and nothing else. No leading or trailing text.
        - Order blocks the way the answer should read top to bottom.
        - Use a single markdown block for ordinary prose answers.
        - For a table, every row object's keys must match the column "key" values.
        - Keep code inside a code block, never inside markdown fences.
        """;

    public static readonly string Text =
        Preamble + "\n\n" + BlockCatalog.PromptSection + "\n\n" + Rules;
}
