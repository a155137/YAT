namespace YAT.Application.Updates;

// Tracks the local calendar date of the last automatic startup update check so YAT checks at most once per day.
public interface IAutomaticUpdateCheckStateStore
{
    DateOnly? ReadLastCheckedDate();

    void WriteLastCheckedDate(DateOnly date);
}

// Fetches plain-text release notes corresponding to a specific UpdateOffer (never an unversioned "latest" endpoint).
// Failures return null so an unavailable release notes endpoint never blocks update notification.
public interface IReleaseNotesSource
{
    Task<string?> FetchAsync(UpdateOffer offer, CancellationToken cancellationToken);
}
