# Contributing

1. Open an issue first for anything larger than a small fix.
2. `dotnet build PackCheck.sln` must succeed with no warnings (warnings are errors), and `dotnet test tests/PackCheck.Tests` must pass.
3. UI text goes in `tools/strings.py`, with all five languages filled in. Run `python tools/strings.py` to regenerate the `.resx` files.
4. New checks: see [docs/rule-authoring.md](docs/rule-authoring.md).
5. Keep pull requests focused, and use Conventional Commit messages (`feat:`, `fix:`, `docs:` ...).
