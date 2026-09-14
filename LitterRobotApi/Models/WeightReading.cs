namespace LitterRobotApi.Models;

/// <summary>A single weight measurement the litter robot recorded for a pet.</summary>
public record WeightReading(string PetId, string PetName, DateTimeOffset Timestamp, double WeightLbs);
