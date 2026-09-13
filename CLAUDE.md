\# YAT Architecture Rules



YAT is a Windows desktop semiconductor data analysis tool.



\## Stack



\* C# / .NET 10

\* Avalonia + MVVM

\* DuckDB for data storage and analytics

\* SkiaSharp for chart rendering

\* xUnit for tests



\## Architecture



YAT.App → YAT.Application → YAT.Domain

YAT.Infrastructure → YAT.Application + YAT.Domain

YAT.Analytics → YAT.Domain



Domain must not depend on UI, DuckDB, SkiaSharp, SQL, or Infrastructure.



\## Data Rule



Raw data must stay in DuckDB. Never load large datasets into ViewModels, ObservableCollection, DataTable, List<T>, or chart renderers.



UI and renderers receive only small result sets or ChartData.



\## Separation



\* ViewModels must not contain SQL.

\* Chart renderers must not contain SQL or business logic.

\* SQL must be isolated in Infrastructure/Application services.

\* Analytics must not depend on Avalonia or Infrastructure.



\## Dependencies



Prefer existing .NET/Avalonia capabilities. Do not add third-party packages without PM approval.



\## Architecture Changes



Do not change the architecture, database, UI framework, or add major dependencies without stopping and asking the PM.



\## Development



Keep YAT offline-first. Do not commit changes unless explicitly instructed.



\## Pending Items



\* YAT.App → YAT.Infrastructure Composition Root exception: allowed in future, but limited to Program.cs and DI registration extension methods. ViewModels and Views must never depend on Infrastructure directly. Not yet established.

\* Architecture testing: deferred. A mechanism to enforce the dependency rules above should be in place before DuckDB is introduced. Tooling not yet selected.

\* YAT.Infrastructure.Tests: deferred. To be created together with the DuckDB integration.


## Task & Review Workflow

Work only within the current PM-approved task scope.

### Small Tasks

For low-risk, localized changes:

1. Implement the complete task.
2. Build / test / run as applicable.
3. Report the final result.
4. Stop for PM review before commit.

Only **one review** is normally required.

### Large Tasks

For architectural, cross-project, or high-impact changes:

1. Inspect the repository and propose an implementation plan.
2. Stop for PM architecture review.
3. After approval, implement the complete task.
4. Build / test / run.
5. Stop for final PM review before commit.

Normally **no more than two reviews** are required.

### Stop Early Only When

Stop and ask PM before continuing if:

* architecture or project dependencies must change;
* an unapproved NuGet package is required;
* task scope must expand;
* destructive or unexpected repository changes are required;
* build/test issues cannot be resolved within the approved design;
* existing PM decisions conflict with the repository.

Do not stop for routine file creation, expected build steps, or implementation details already covered by the approved task.

Never commit unless explicitly approved by the PM.




