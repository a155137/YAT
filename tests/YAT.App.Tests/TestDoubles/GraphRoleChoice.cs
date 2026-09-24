using YAT.app.ViewModels;

namespace YAT.App.Tests.TestDoubles;

// Picks a column for a role the way the setup does: in the role's list when it takes several columns, in its selector
// when it takes one. From Task #041 on, the variables of every graph but the scatter plot are picked from a list.
internal static class GraphRoleChoice
{
    public static void Choose(this GraphRoleViewModel role, GraphColumnOption? option)
    {
        if (!role.AllowsMultiple)
        {
            role.SelectedOption = option;
        }
        else if (option is not null && !role.SelectedOptions.Contains(option))
        {
            role.SelectedOptions.Add(option);
        }
    }
}
