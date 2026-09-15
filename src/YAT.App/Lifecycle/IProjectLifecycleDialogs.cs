namespace YAT.app.Lifecycle;

// The user interaction the project lifecycle needs: file pickers, the save prompt and error messages. The desktop
// implementation uses Avalonia dialogs; tests use fakes.
public interface IProjectLifecycleDialogs
{
    // A single existing YAT project file (*.yat), or null when the user cancels.
    Task<string?> PickProjectToOpenAsync();

    // A location to save the project to (default extension .yat), or null when the user cancels. The file may exist; YAT
    // never overwrites it.
    Task<string?> PickSaveLocationAsync(string suggestedFileName);

    // Asks whether to save the temporary project before it is discarded: before closing YAT (closing) or before
    // continuing with New or Open.
    Task<SaveChangesChoice> AskSaveChangesAsync(string projectName, bool closing);

    Task ShowErrorAsync(string message);
}

public enum SaveChangesChoice
{
    // The default: a prompt closed without a choice cancels.
    Cancel,

    Save,

    Discard
}
