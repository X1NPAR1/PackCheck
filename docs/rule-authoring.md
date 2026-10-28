# Writing a check

A check is a class that implements `IAnalyzer` (`src/PackCheck.Validation/Analysis.cs`):

```csharp
public sealed class MotdLengthAnalyzer : IAnalyzer
{
    public string Id => "PC060";

    public IEnumerable<CheckResult> Analyze(PackContext context)
    {
        if (!context.ServerProperties.TryGetValue("motd", out var motd))
        {
            yield return new CheckResult { Id = Id, Name = "MOTD", Status = CheckStatus.Skip, Description = "No motd set.", NotRunReason = "Key missing" };
            yield break;
        }

        yield return new CheckResult
        {
            Id = Id,
            Name = "MOTD length",
            Status = motd.Length <= 59 ? CheckStatus.Pass : CheckStatus.Warn,
            Description = $"motd is {motd.Length} characters.",
            Evidence = [$"motd={motd}"],
            AffectedFile = "server.properties",
            Fix = motd.Length <= 59 ? null : new SuggestedFix("Shorten the motd; long lines are cut off in the server list.", false),
        };
    }
}
```

Then add it to `PackValidator.DefaultAnalyzers`.

Rules:

- **Evidence over guesses.** Put the exact values you based the result on in `Evidence`. Never infer compatibility from file names.
- Use `Skip` with a `NotRunReason` when the input you need is missing. Use `Fail` only when you can show the problem.
- Set `SuggestedFix.IsCertain` to `false` when the fix is a likely remedy rather than a guaranteed one.
- Analyzers must not modify files, start processes or use the network.
- An exception thrown by an analyzer becomes an `Error` result. It does not crash the run, but write tests anyway: see `tests/PackCheck.Tests/ValidationTests.cs` and `PackBuilder`.
