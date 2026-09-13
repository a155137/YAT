using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Domain.Tests;

public class WorksheetColumnTests
{
    [Fact]
    public void AssignedValuesArePreserved()
    {
        var id = Guid.NewGuid();
        var worksheetId = Guid.NewGuid();

        var column = new WorksheetColumn
        {
            Id = id,
            WorksheetId = worksheetId,
            Index = 7,
            Name = "Vth",
            DataType = WorksheetDataType.Numeric,
            SemanticType = ColumnSemanticType.TestParameter,
            Unit = "mV"
        };

        Assert.Equal(id, column.Id);
        Assert.Equal(worksheetId, column.WorksheetId);
        Assert.Equal(7, column.Index);
        Assert.Equal("Vth", column.Name);
        Assert.Equal(WorksheetDataType.Numeric, column.DataType);
        Assert.Equal(ColumnSemanticType.TestParameter, column.SemanticType);
        Assert.Equal("mV", column.Unit);
    }

    [Fact]
    public void SemanticTypeIsUnclassifiedUntilAssigned()
    {
        var column = new WorksheetColumn { Name = "RawValue" };

        Assert.Null(column.SemanticType);

        column.SemanticType = ColumnSemanticType.Wafer;
        Assert.Equal(ColumnSemanticType.Wafer, column.SemanticType);

        column.SemanticType = null;
        Assert.Null(column.SemanticType);
    }

    [Fact]
    public void UnitIsOptional()
    {
        var column = new WorksheetColumn
        {
            Name = "Lot",
            DataType = WorksheetDataType.String,
            SemanticType = ColumnSemanticType.Lot
        };

        Assert.Null(column.Unit);
    }
}
