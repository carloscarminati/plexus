using Xunit;

// Run test classes serially. The schemas (BlockCatalog, ReasoningSchemas) are
// JsonSchema.Net instances whose internal evaluation state is built lazily and is NOT
// thread-safe on that first build; parallel xUnit classes hitting an un-warmed schema
// on a cold build could race (a rare flaky failure). Each schema warms itself in its
// static initializer, but serial execution removes the race by construction — the suite
// is ~200ms, so the cost is nil and the green gate becomes deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
