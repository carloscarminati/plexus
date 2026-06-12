namespace Plexus.Sidecar.Contract;

// ADR-0002 R2.0b — a recipe is DATA, not core (ADR: "recipes are config, not core").
// A recipe is an ordered list of steps over the reasoning primitives; the executor
// interprets it generically — there is no hardcoded step logic. The investigator
// process is the first instance (Recipes.Investigator), not baked into the engine.
public sealed class Recipe
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<RecipeStep> Steps { get; set; } = new();
}

public sealed class RecipeStep
{
    public string Id { get; set; } = "";       // step identifier (also the node-id prefix)
    public string Role { get; set; } = "";      // ReasoningRoles value the produced node(s) carry
    public string Prompt { get; set; } = "";    // the step instruction fed to the model

    // Cardinality (ADR-0002 R2.0b): multi-node steps emit a bounded array; single-node
    // steps emit one object. The BOUNDS live here, in config — a complex domain may want
    // a 2..8 hypothesis fan-out, a simple one 2..4. They are NOT engine constants.
    public bool Array { get; set; }
    public int MinItems { get; set; } = 1;
    public int? MaxItems { get; set; }

    // Human decision point (hypothesis fan-out, evaluation/escalate, fact gate). The
    // seam exists now; the interactive UI is M1. The first run auto-resolves it.
    public bool DecisionSeam { get; set; }
}

// The investigator recipe (config data, not core): understand → extract facts →
// uncertainties → hypotheses → contrast → conclude. "Explain evidence" is a
// projection over edges (compose), not a node. Bounds are illustrative defaults a
// domain recipe overrides.
public static class Recipes
{
    public static Recipe Investigator { get; } = new()
    {
        Id = "investigator",
        Label = "Expert investigator",
        Steps = new()
        {
            new() { Id = "frame", Role = ReasoningRoles.Frame,
                    Prompt = "State the case: question, scope, constraints.\nReply as JSON: {\"question\":\"…\",\"scope\":\"…\"}" },
            new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1,
                    Prompt = "Extract the atomic facts, each with its source.\nReply as JSON: {\"facts\":[{\"claim\":\"…\",\"sourceKind\":\"doc|api|given\",\"sourceRef\":\"…\"}]}" },
            new() { Id = "uncertainties", Role = ReasoningRoles.Uncertainty, Array = true, MinItems = 1,
                    Prompt = "List the gaps / unknowns.\nReply as JSON: {\"uncertainties\":[{\"question\":\"…\"}]}" },
            new() { Id = "hypotheses", Role = ReasoningRoles.Hypothesis, Array = true, MinItems = 2, MaxItems = 6,
                    Prompt = "Propose 2–6 candidate explanations covering the plausible space, each addressing an uncertainty.\nReply as JSON: {\"hypotheses\":[{\"statement\":\"…\",\"addresses\":[\"u0\"]}]}", DecisionSeam = true },
            new() { Id = "evaluation", Role = ReasoningRoles.Evaluation,
                    Prompt = "Weigh the facts for/against each hypothesis (weight 0–1 magnitude), and give a rationale: which evidence tips the balance and why the best-supported hypothesis beats the rivals (refer to facts/hypotheses by their ids).\nReply as JSON: {\"weighings\":[{\"fact\":\"f0\",\"hypothesis\":\"h0\",\"stance\":\"supports|refutes\",\"weight\":0.0}],\"rationale\":\"…\"}", DecisionSeam = true },
            new() { Id = "conclusion", Role = ReasoningRoles.Conclusion,
                    Prompt = "Select the best-supported hypothesis and cite the facts.\nReply as JSON: {\"selects\":\"h0\",\"cites\":[\"f0\"],\"summary\":\"…\"}" },
        },
    };
}
