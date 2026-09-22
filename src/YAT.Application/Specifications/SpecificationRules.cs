namespace YAT.Application.Specifications;

// One part of a specification, so a problem can point at the value the user has to correct.
public enum SpecificationField
{
    LowerLimit,
    Target,
    UpperLimit
}

public enum SpecificationProblemKind
{
    // NaN or an infinity: not a value a specification can hold.
    NotFinite,

    // The lower limit is not below the upper one. Equal limits leave no room at all, so they are out of order too.
    LimitsOutOfOrder,

    // The target lies below the lower limit or above the upper one. On a limit is allowed.
    TargetOutsideLimits
}

// What is wrong with a specification, and which of its values is to blame: the value itself when it is not finite,
// the upper limit when the limits are out of order, the target when it lies outside them.
public sealed record SpecificationProblem(SpecificationProblemKind Kind, SpecificationField Field);

// The rules every specification obeys, wherever it is used:
//
//     every value that is given is finite;
//     with both limits, LSL < USL;
//     with a target, LSL <= Target when there is a lower limit and Target <= USL when there is an upper one.
//
// Every value is optional, so an empty specification is valid. Rules that belong to one use only - a capability
// analysis needs at least one limit to measure against - stay with that use.
public static class SpecificationRules
{
    public static IReadOnlyList<SpecificationProblem> Check(Specification specification)
    {
        ArgumentNullException.ThrowIfNull(specification);

        var problems = new List<SpecificationProblem>();
        var lower = Finite(problems, specification.LowerLimit, SpecificationField.LowerLimit);
        var target = Finite(problems, specification.Target, SpecificationField.Target);
        var upper = Finite(problems, specification.UpperLimit, SpecificationField.UpperLimit);

        if (lower is { } lowerLimit && upper is { } upperLimit && lowerLimit >= upperLimit)
        {
            problems.Add(new SpecificationProblem(SpecificationProblemKind.LimitsOutOfOrder, SpecificationField.UpperLimit));
        }

        if (target is { } aim && ((lower is { } floor && aim < floor) || (upper is { } ceiling && aim > ceiling)))
        {
            problems.Add(new SpecificationProblem(SpecificationProblemKind.TargetOutsideLimits, SpecificationField.Target));
        }

        return problems;
    }

    public static bool IsValid(Specification specification) => Check(specification).Count == 0;

    // A value the other rules can compare, or null after reporting why it cannot be one.
    private static double? Finite(List<SpecificationProblem> problems, double? value, SpecificationField field)
    {
        if (value is not { } number)
        {
            return null;
        }

        if (double.IsFinite(number))
        {
            return number;
        }

        problems.Add(new SpecificationProblem(SpecificationProblemKind.NotFinite, field));
        return null;
    }
}
