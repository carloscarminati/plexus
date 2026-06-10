using Microsoft.Extensions.AI;

namespace Plexus.Sidecar.Tests;

// A scripted IChatClient for emission tests: returns canned replies in order (then
// "{}" once exhausted), counting calls so a test can assert how many times the
// auto-fix loop re-prompted. No real provider — keeps the emission machinery isolated.
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<string> _replies;
    public int Calls { get; private set; }

    // The first user message of each call — the step instruction (a test can assert the
    // ref context was injected). Retries share the same first message.
    public List<string> Instructions { get; } = new();

    public ScriptedChatClient(params string[] replies) => _replies = new Queue<string>(replies);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        Instructions.Add(messages.FirstOrDefault()?.Text ?? "");
        var text = _replies.Count > 0 ? _replies.Dequeue() : "{}";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
