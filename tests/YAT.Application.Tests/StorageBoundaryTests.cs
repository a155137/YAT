using System.Reflection;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;

namespace YAT.Application.Tests;

// Architecture guards for the raw-data storage contract. They inspect signatures only, not implementations.
public class StorageBoundaryTests
{
    private static readonly Type[] RawDataContractTypes =
    [
        typeof(IWorksheetRawDataStore),
        typeof(RawDataBlock),
        typeof(RawDataColumn),
        typeof(NumericRawDataColumn),
        typeof(StringRawDataColumn)
    ];

    private static readonly Type[] MetadataRepositoryTypes =
    [
        typeof(IProjectRepository),
        typeof(IWorksheetRepository),
        typeof(IWorksheetColumnRepository)
    ];

    private static HashSet<Type> ReferencedTypes(IEnumerable<Type> contractTypes)
    {
        var referenced = new HashSet<Type>();

        void Add(Type type)
        {
            if (!referenced.Add(type))
            {
                return;
            }

            if (type.HasElementType)
            {
                Add(type.GetElementType()!);
            }

            foreach (var argument in type.GetGenericArguments())
            {
                Add(argument);
            }
        }

        foreach (var contractType in contractTypes)
        {
            const BindingFlags publicMembers = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var method in contractType.GetMethods(publicMembers).Where(method => !method.IsSpecialName))
            {
                Add(method.ReturnType);
                foreach (var parameter in method.GetParameters())
                {
                    Add(parameter.ParameterType);
                }
            }

            foreach (var constructor in contractType.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    Add(parameter.ParameterType);
                }
            }

            foreach (var property in contractType.GetProperties(publicMembers))
            {
                Add(property.PropertyType);
            }
        }

        return referenced;
    }

    [Fact]
    public void RawDataContractDoesNotDependOnIngestionModels()
    {
        var referenced = ReferencedTypes(RawDataContractTypes);

        Assert.DoesNotContain(typeof(ParsedTabularData), referenced);
        Assert.DoesNotContain(referenced, type => type.Namespace == typeof(ParsedTabularData).Namespace);
    }

    [Fact]
    public void RawDataContractUsesOnlyBaseLibraryAndApplicationOwnedTypes()
    {
        string[] allowedNamespaces =
        [
            "System",
            "System.Collections.Generic",
            "System.Threading",
            "System.Threading.Tasks",
            "YAT.Application.Abstractions.Persistence",
            "YAT.Domain.Enums"
        ];

        var unexpectedNamespaces = ReferencedTypes(RawDataContractTypes)
            .Select(type => type.Namespace ?? "(global)")
            .Distinct()
            .Except(allowedNamespaces);

        Assert.Empty(unexpectedNamespaces);
    }

    [Fact]
    public void RawDataColumnsCarryOnlyIdentityTypeAndValues()
    {
        Assert.Equal(["ColumnId", "DataType", "RowCount"], PropertyNames(typeof(RawDataColumn)));
        Assert.Equal(["ColumnId", "DataType", "RowCount", "Values"], PropertyNames(typeof(NumericRawDataColumn)));
        Assert.Equal(["ColumnId", "DataType", "RowCount", "Values"], PropertyNames(typeof(StringRawDataColumn)));
        Assert.Equal(["Columns", "RowCount"], PropertyNames(typeof(RawDataBlock)));
    }

    [Fact]
    public void RawDataStoreIsSeparateFromMetadataRepositories()
    {
        Assert.Empty(typeof(IWorksheetRawDataStore).GetInterfaces());
        Assert.All(MetadataRepositoryTypes, repository => Assert.False(repository.IsAssignableFrom(typeof(IWorksheetRawDataStore))));

        var metadataReferences = ReferencedTypes(MetadataRepositoryTypes);
        Assert.All(RawDataContractTypes, rawType => Assert.DoesNotContain(rawType, metadataReferences));
    }

    private static string[] PropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
