namespace Plexus.Sidecar.Routing;

// Keeps callers depending on a single IModelRouter while dispatching by policy:
// manual -> ManualRouter (explicit choice, always wins), auto -> HeuristicRouter.
// The interface and the two concrete routers are unchanged.
public sealed class CompositeRouter : IModelRouter
{
    private readonly ManualRouter _manual;
    private readonly HeuristicRouter _heuristic;

    public CompositeRouter(ManualRouter manual, HeuristicRouter heuristic)
    {
        _manual = manual;
        _heuristic = heuristic;
    }

    public Task<ModelChoice> SelectModelAsync(RoutingContext ctx, CancellationToken ct = default) =>
        ctx.Policy.Kind == "auto"
            ? _heuristic.SelectModelAsync(ctx, ct)
            : _manual.SelectModelAsync(ctx, ct);
}
