using LitterRobotApi.Models;

namespace LitterRobotApi.Interfaces;

/// <summary>Reads pet data from Whisker's Litter-Robot API.</summary>
public interface ILitterRobotClient
{
    /// <summary>Lists the pets registered on the account.</summary>
    Task<IReadOnlyList<LitterRobotPet>> GetPetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one pet's weight history, oldest first. <paramref name="limit"/> caps how many
    /// readings the API returns; zero or less uses the default of 100.
    /// </summary>
    Task<IReadOnlyList<WeightReading>> GetWeightHistoryAsync(
        LitterRobotPet pet,
        int limit = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every pet's weight history, oldest first. <paramref name="limit"/> caps how many
    /// readings the API returns per pet; zero or less uses the default of 100.
    /// </summary>
    Task<IReadOnlyList<WeightReading>> GetAllWeightHistoryAsync(
        int limit = 0,
        CancellationToken cancellationToken = default);
}
