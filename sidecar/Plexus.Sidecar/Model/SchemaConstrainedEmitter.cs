using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;

namespace Plexus.Sidecar.Model;

// ADR-0002 R2.0a — provider-agnostic schema-constrained emission with bounded auto-fix.
//
// A recipe step must emit STRUCTURALLY valid JSON (e.g. a fact with claim/sourceKind/
// sourceRef) or the recipe breaks — unlike the render path, which may degrade an
// invalid block to markdown. So the flow is: emit → validate against the step schema →
// on failure, re-prompt the model WITH the validation errors (bounded) → if it never
// validates, return an EXPLICIT error (never a silent markdown fallback).
//
// Re-prompt is the universal mechanism (works on the provider-generic loop, and is the
// fallback for providers without constrained decoding). ResponseFormat (per-provider
// constrained decoding) is an OPT-IN that only shortcuts the structural loop — it
// guarantees the output matches the schema, never the R1 semantic invariants — so it
// never removes the validator or the re-prompt path. It is plumbed here but not required.
public sealed record SchemaEmissionResult(
    bool Ok,
    JsonNode? Value,
    int Attempts,
    IReadOnlyList<string> Errors,
    string? Error,
    // Instrumentation (ADR-0002 R2.0b smoke): how many attempts failed each way. The
    // two are counted separately because they say different things about the model tier
    // — structural = "didn't match the shape", referential = "invented a bad ref".
    int StructuralFailures = 0,
    int PostStructuralFailures = 0);

public static class SchemaConstrainedEmitter
{
    public static async Task<SchemaEmissionResult> EmitAsync(
        IChatClient client,
        string modelId,
        string instruction,
        JsonSchema schema,
        int maxAttempts = 3,
        ChatResponseFormat? responseFormat = null, // opt-in constrained decoding; loop never depends on it
        // Optional check run AFTER structural validation, for constraints the schema
        // can't express (referential integrity of emitted refs; grounding fidelity). Async
        // because a check may itself call a model (the fidelity judge). Its errors feed the
        // SAME bounded re-prompt loop — so a real model's bad ref / laundered claim is
        // corrected, or surfaced explicitly on exhaustion. Never silently dropped.
        Func<JsonNode, CancellationToken, Task<IReadOnlyList<string>>>? postStructuralCheck = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, instruction) };
        var options = new ChatOptions { ModelId = modelId, ResponseFormat = responseFormat };
        IReadOnlyList<string> lastErrors = new[] { "no attempt made" };
        int structuralFailures = 0, postStructuralFailures = 0;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var response = await client.GetResponseAsync(messages, options, ct);
            var json = ExtractJsonObject(response.Text ?? string.Empty);

            if (json is null)
            {
                lastErrors = new[] { "output was not valid JSON" };
                structuralFailures++;
            }
            else if (!JsonSchemaGen.Validate(schema, json, out var errors))
            {
                lastErrors = errors;
                structuralFailures++;
            }
            else
            {
                var checkErrors = postStructuralCheck is null
                    ? Array.Empty<string>()
                    : await postStructuralCheck(json, ct);
                if (checkErrors.Count == 0)
                    return new SchemaEmissionResult(true, json, attempt, Array.Empty<string>(), null,
                        structuralFailures, postStructuralFailures);
                lastErrors = checkErrors;
                postStructuralFailures++;
            }

            // Auto-fix: echo the bad turn and feed the errors back for a correction.
            messages.AddRange(response.Messages);
            messages.Add(new ChatMessage(ChatRole.User,
                "Your previous reply was rejected:\n- "
                + string.Join("\n- ", lastErrors)
                + "\nReturn ONLY corrected JSON that satisfies all the requirements."));
        }

        return new SchemaEmissionResult(
            false, null, maxAttempts, lastErrors,
            $"Emission did not satisfy the schema after {maxAttempts} attempt(s).",
            structuralFailures, postStructuralFailures);
    }

    // Tolerant extraction of the outermost JSON object: strip a ```json fence if one
    // slipped in, else take the outer {...}. Returns null if there's no parseable object.
    private static JsonNode? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();

        var fence = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            var afterFence = trimmed.IndexOf('\n', fence);
            var fenceEnd = trimmed.IndexOf("```", fence + 3, StringComparison.Ordinal);
            if (afterFence > 0 && fenceEnd > afterFence)
                trimmed = trimmed[(afterFence + 1)..fenceEnd].Trim();
        }

        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first < 0 || last <= first)
            return null;

        try { return JsonNode.Parse(trimmed[first..(last + 1)]); }
        catch { return null; }
    }
}
