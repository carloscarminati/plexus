using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;

namespace Plexus.Sidecar.Model;

// ADR-0002 R2.2.0-fidelity — the real fidelity judge: a separate, narrow LLM call that
// answers only "does the source support this claim?". Distinct from the extracting model
// (a second opinion enforcing discipline). Can run on a stronger model than the recipe.
public sealed class LlmFidelityJudge : IFidelityJudge
{
    private readonly IChatClient _client;
    private readonly string _modelId;

    public LlmFidelityJudge(IChatClient client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public async Task<bool> IsSupportedAsync(string claim, string sourceText, CancellationToken ct = default)
    {
        var prompt =
            $"SOURCE:\n{sourceText}\n\nCLAIM:\n{claim}\n\n"
            + "Is the CLAIM directly supported by the SOURCE? A claim is supported only if the "
            + "source states it or directly entails it — NOT if it is merely on a related topic. "
            + "Answer with exactly one word: YES or NO.";

        var response = await _client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, prompt) },
            new ChatOptions { ModelId = _modelId, MaxOutputTokens = 8 },
            ct);

        return (response.Text ?? "").TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
    }
}
