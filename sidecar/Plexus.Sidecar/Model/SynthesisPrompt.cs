namespace Plexus.Sidecar.Model;

// X1 — the synthesis instruction appended as the final user turn. The system prompt
// (block emission, the catalog) still governs FORMAT; this governs CONTENT: read the
// branching exploration above and WRITE THE DECISION as a structured brief — do not
// summarize or concatenate the branches. Grounding-agnostic: it works over whatever
// grounded the branches (Context7 / a design-systems MCP / web / nothing).
public static class SynthesisPrompt
{
    public const string Instruction = """
        Everything above is a branching exploration of a decision — several options
        explored in parallel, with their facts and trade-offs. Converge it into a
        DECISION BRIEF. Do NOT summarize or concatenate the branches; READ them and
        WRITE the decision.

        Emit your turn as blocks (the usual JSON object), structured as a decision
        brief in this order:
          1. markdown — an H1 title, then one sentence stating the decision / question.
          2. markdown — the options that were considered (a short list).
          3. table — a side-by-side comparison across the dimensions that matter
             (e.g. fit, maturity, cost, complexity, risk). Add a chart block too ONLY
             if it genuinely clarifies the comparison.
          4. markdown — what was ruled out, and why.
          5. markdown — "## Recommendation": the chosen option, the rationale, and the
             caveats (when the call would change).
          6. markdown — "## Sources": the grounded facts / links the exploration relied
             on (from the content above).

        Base everything ONLY on the exploration above. If the branches are unrelated or
        don't support a clear decision, say so honestly in the recommendation instead
        of inventing one. Output ONLY the block JSON, as instructed.
        """;
}
