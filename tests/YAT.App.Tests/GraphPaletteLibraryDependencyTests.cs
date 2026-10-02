using System.Reflection;
using System.Reflection.Emit;
using YAT.Application.Abstractions.Settings;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.Views;

namespace YAT.App.Tests;

// Architecture guard (Task #050): a graph is drawn, presented, copied and exported from the colours it holds. Nothing that
// draws, presents or exports it may know the user's palette library, where it is kept, or the Infrastructure that keeps
// it - not in a signature, a field or a method body - so a change to the library can never reach a graph after its
// setup took its colours.
public class GraphPaletteLibraryDependencyTests
{
    private static readonly Type[] Forbidden =
    [
        typeof(GraphPaletteLibrary),
        typeof(GraphPaletteLibraryService),
        typeof(GraphPaletteDefinition),
        typeof(GraphPaletteLibraryResult),
        typeof(GraphPaletteChange),
        typeof(IGraphPaletteLibraryStore),
        typeof(GraphPaletteLibraryLoadResult)
    ];

    private static readonly string[] ForbiddenNamespaces = ["YAT.Application.Abstractions.Settings", "YAT.Infrastructure"];

    // Everything that draws, presents, copies or exports a graph: the rendering and export namespaces, and the canvas
    // that resolves the appearance on screen.
    internal static IEnumerable<Type> DrawingTypes() =>
        typeof(GraphCanvas).Assembly.GetTypes()
            .Where(type => type.Namespace is "YAT.app.Graphs.Rendering" or "YAT.app.Graphs.Export")
            .Append(typeof(GraphCanvas));

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    // The types a type refers to: its base, interfaces, fields, properties, method signatures, and every type and member
    // its method bodies name.
    internal static IEnumerable<Type> ReferencedBy(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var contract in type.GetInterfaces())
        {
            yield return contract;
        }

        foreach (var field in type.GetFields(All))
        {
            yield return field.FieldType;
        }

        foreach (var property in type.GetProperties(All))
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
        {
            if (method is MethodInfo info)
            {
                yield return info.ReturnType;
            }

            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }

            foreach (var used in InBody(method))
            {
                yield return used;
            }
        }
    }

    private static IEnumerable<Type> InBody(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var position = 0;
        while (position < il.Length)
        {
            var value = (short)il[position++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[position++]));
            }

            var code = OpCodesByValue[value];
            switch (code.OperandType)
            {
                case OperandType.InlineMethod:
                case OperandType.InlineField:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    var token = BitConverter.ToInt32(il, position);
                    position += 4;
                    MemberInfo? member = null;
                    try
                    {
                        member = method.Module.ResolveMember(
                            token,
                            method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null,
                            method.IsGenericMethod ? method.GetGenericArguments() : null);
                    }
                    catch (ArgumentException)
                    {
                    }

                    switch (member)
                    {
                        case Type used:
                            yield return used;
                            break;
                        case FieldInfo field:
                            yield return field.DeclaringType!;
                            yield return field.FieldType;
                            break;
                        case MethodBase called:
                            yield return called.DeclaringType!;
                            if (called is MethodInfo calledInfo)
                            {
                                yield return calledInfo.ReturnType;
                            }

                            foreach (var parameter in called.GetParameters())
                            {
                                yield return parameter.ParameterType;
                            }

                            break;
                    }

                    break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, position);
                    position += 4 + (4 * count);
                    break;
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position += 1;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                default:
                    position += 4;
                    break;
            }
        }
    }

    internal static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType)
        {
            foreach (var element in Expand(type.GetElementType()!))
            {
                yield return element;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Expand))
            {
                yield return argument;
            }
        }
    }

    [Fact]
    public void NothingThatDrawsPresentsOrExportsAGraphKnowsThePaletteLibraryOrWhereItIsKept()
    {
        var offenders = DrawingTypes()
            .SelectMany(type => ReferencedBy(type).Where(used => used is not null).SelectMany(Expand)
                .Where(used => Forbidden.Contains(used)
                    || ForbiddenNamespaces.Any(prefix => (used.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)))
                .Select(used => $"{type.FullName} -> {used.FullName}"))
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }

    // A graph window, and the presenter that opens it, reach the palettes only through their choices and the Palette
    // Manager (IGraphPaletteLibraryAccess): not the library service, not the concrete access over it, and not where the
    // palettes are kept.
    [Fact]
    public void AGraphWindowKnowsPaletteChoicesOnlyNeverTheLibraryOrItsStorage()
    {
        Type[] windowForbidden = [.. Forbidden, typeof(YAT.app.Graphs.GraphPaletteLibraryAccess)];
        var offenders = new[] { typeof(GraphWindow), typeof(AvaloniaGraphWindowPresenter) }
            .SelectMany(type => ReferencedBy(type).Where(used => used is not null).SelectMany(Expand)
                .Where(used => windowForbidden.Contains(used)
                    || ForbiddenNamespaces.Any(prefix => (used.Namespace ?? string.Empty).StartsWith(prefix, StringComparison.Ordinal)))
                .Select(used => $"{type.FullName} -> {used.FullName}"))
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
        Assert.Contains(typeof(YAT.app.Graphs.IGraphPaletteLibraryAccess), ReferencedBy(typeof(GraphWindow)).SelectMany(Expand));
    }

    [Fact]
    public void TheGuardSeesWhatItLooksFor()
    {
        // The scan finds a type a method body only names: GraphAppearance.Resolve uses GraphTheme and GraphAppearanceOptions.
        var used = ReferencedBy(typeof(GraphAppearance)).SelectMany(Expand).ToHashSet();

        Assert.Contains(typeof(GraphTheme), used);
        Assert.Contains(typeof(GraphAppearanceOptions), used);
        Assert.Contains(DrawingTypes(), type => type == typeof(GraphExportService));
        Assert.Contains(DrawingTypes(), type => type == typeof(SkiaGraphRenderer));

        // And it would find the library where it is meant to be used: the windows' access to it is built over it.
        Assert.Contains(typeof(GraphPaletteLibraryService), ReferencedBy(typeof(YAT.app.Graphs.GraphPaletteLibraryAccess)).SelectMany(Expand));
    }
}
