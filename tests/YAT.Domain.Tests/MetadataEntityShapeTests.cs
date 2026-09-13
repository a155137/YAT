using System.Collections;
using YAT.Domain.Entities;

namespace YAT.Domain.Tests;

// Raw measurement values must never live on worksheet metadata entities.
public class MetadataEntityShapeTests
{
    [Theory]
    [InlineData(typeof(Worksheet))]
    [InlineData(typeof(WorksheetColumn))]
    public void WorksheetMetadataEntitiesHaveNoCollectionProperties(Type entityType)
    {
        var collectionProperties = entityType.GetProperties()
            .Where(property => property.PropertyType != typeof(string)
                && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name);

        Assert.Empty(collectionProperties);
    }
}
