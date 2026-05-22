# Runtime Intelligence Graph

Working repository for a CLI-first .NET code-mining tool.

The project aims to index .NET solutions into immutable SQLite runs, then expose
entrypoint callgraphs annotated with interesting external effects such as HTTP,
EF Core, Redis, and loop/parallel execution contexts.

The current product direction is captured in [docs/mvp-spec.md](docs/mvp-spec.md).
