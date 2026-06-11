namespace Plexus.Sidecar.Contract;

// ADR-0002 Rx.2.0 — the human decision seam. An adjudication is a GENERIC review
// primitive: a reviewer records accept/reject over a loaded graph, with an optional note,
// attributed + timestamped. It carries NO regime semantics — CGR (or any workflow) is a
// CONSUMER of this primitive, not its mould; these fields and nothing more.
//
// The property that matters: an adjudication is ADDITIVE metadata ABOUT a graph — it is
// NOT a reasoning node, and recording one never mutates the reasoning (nodes / edges /
// R1 diagnostics are untouched). Accepting a net-negative graph does NOT clear the flag;
// the flag stays and the human note sits beside it. "Flagged AND accepted-with-reason",
// both visible — that is the auditable artefact.
public static class AdjudicationDecisions
{
    public const string Accept = "accept";
    public const string Reject = "reject";

    public static bool IsValid(string? decision) => decision is Accept or Reject;
}

public sealed class Adjudication
{
    public string Decision { get; set; } = AdjudicationDecisions.Accept; // "accept" | "reject"
    public string? Note { get; set; }
    public string Reviewer { get; set; } = ""; // attribution — placeholder identity for now (real identity + signature deferred)
    public string Timestamp { get; set; } = ""; // ISO-8601, stamped server-side at write
}
