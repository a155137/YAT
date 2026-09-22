namespace YAT.Application.Specifications;

// The specification a measurement is held to: the lowest and the highest value it may take, and the value it is aimed
// at. Every part is optional - a one-sided specification has a single limit, and a target may be given on its own.
//
// It is data about the measurement, not about how a graph looks, so it is shared: a graph draws it, and later a
// capability analysis can measure against the same type. Whether the values make sense together is decided by
// SpecificationRules, never here, so a specification the user typed can always be represented and then explained.
public sealed record Specification(double? LowerLimit = null, double? Target = null, double? UpperLimit = null)
{
    // No limit and no target.
    public static Specification None { get; } = new();

    public bool IsEmpty => LowerLimit is null && Target is null && UpperLimit is null;
}
