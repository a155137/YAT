# YAT

**YuXiang Analysis Tool** is a Windows desktop application for engineering data analysis and visualization.

YAT is built for fast, practical engineering workflows: paste tabular data into a worksheet, choose a graph or an
analysis, and get the result in a few clicks. The emphasis is on responsive graphing of large datasets and on the
statistics engineers use day to day.

YAT is an early release and is under active development.

## Features

### Graphs

- Scatter Plot
- Histogram
- Normal Probability Plot
- Empirical CDF
- Box Plot

### Analysis

- Descriptive Statistics (N, missing, mean, standard deviation, minimum, quartiles, median, maximum)
- Capability Analysis (Cp, Cpl, Cpu and Cpk from the within standard deviation, against specification limits)

### Workflow

- **Worksheets and projects.** Data lives in worksheets inside a project, which is saved as a single `.yat` file.
- **Paste tabular data.** Copy a table with a header row from Excel or a text file and paste it into a worksheet.
- **Grouping.** Graphs can split their data by a categorical column, drawing one series per group.
- **Several variables.** Histograms, probability plots, empirical CDFs and box plots can show several variables in
  one graph or in separate graphs.
- **Row filtering.** A graph can be limited to the rows whose value in a chosen column is one of the selected values,
  without changing the worksheet.
- **Labels and legends.** Graph titles and axis titles can be automatic, custom or hidden; the legend can be hidden or
  moved.
- **Statistics panels.** Histograms, probability plots and empirical CDFs can show the mean, standard deviation and N
  of each series beside the plot.
- **Specification limits.** LSL, target and USL can be drawn on distribution graphs.
- **Appearance and palettes.** Series colors, grid and backgrounds can be changed per graph. Custom color palettes can
  be saved and one of them chosen as the default for new graphs; the built-in YAT Default palette is always available.
- **Editing after drawing.** Labels, axis ranges, legend, statistics panel, appearance and box plot options can be
  changed from a drawn graph's context menu, through dialogs.
- **Copy and export.** Graphs can be copied to the clipboard as an image, or exported as PNG or as a PowerPoint
  (`.pptx`) slide.

## Technology

YAT is written in C# on .NET 10.

| Component  | Role                                   |
|------------|----------------------------------------|
| Avalonia   | Desktop user interface                 |
| SkiaSharp  | Graph rendering                        |
| DuckDB     | Worksheet and project data storage     |

## Platform

YAT currently targets **Windows x64**. Release packages are self-contained: they include the .NET runtime and do not
require an installer.

Other operating systems are not supported at this time.

## Current version

The current tagged version is
[**v0.1.0**](https://github.com/a155137/YAT/releases/tag/v0.1.0). It is a Git tag; no packaged release download is
published on GitHub yet.

## Build from source

Requirements: Windows x64 and the .NET 10 SDK.

From the repository root:

```bash
dotnet restore
dotnet build
dotnet test
```

To run the application:

```bash
dotnet run --project src/YAT.App
```

The solution file is [`YAT.slnx`](YAT.slnx). Application projects are under [`src/`](src) and test projects under
[`tests/`](tests).

## License

YAT is licensed under the **GNU Affero General Public License v3.0**. See [`LICENSE.txt`](LICENSE.txt).

Third-party components included with YAT remain under their own licenses; see
[`build/docs/THIRD-PARTY-NOTICES.txt`](build/docs/THIRD-PARTY-NOTICES.txt).

## Project status

YAT is under active development. The current public tagged version is v0.1.0.
