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



