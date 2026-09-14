using LitterRobotApi.Exceptions;
using LitterRobotApi.Interfaces;
using LitterRobotApi.Models;
using LitterRobotApi.Support.Constants;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LitterRobotApi.Implementations;

// Ported from pylitterbot: signs in to Whisker's Cognito pool, then reads pet
// data from the pet-profile GraphQL API.
//
// pylitterbot authenticates via SRP (pycognito). The app client also permits
// USER_PASSWORD_AUTH, so we use a plain InitiateAuth call instead and avoid
// taking an AWS SDK dependency for the SRP handshake.

/// <summary>Reads pet data from Whisker's Litter-Robot API.</summary>
public class LitterRobotClient : ILitterRobotClient
{
    private static readonly HttpClient SharedHttpClient = new();

    private const string PetsQuery = """
        query GetPetsByUser($userId: String!) {
            getPetsByUser(userId: $userId) {
                petId
                name
            }
        }
        """;

    private const string WeightHistoryQuery = """
        query GetWeightHistoryByPetId($petId: String!, $limit: Int) {
            getWeightHistoryByPetId(petId: $petId, limit: $limit) {
                weight
                timestamp
            }
        }
        """;

    private readonly LitterRobotCredentials credentials;
    private readonly HttpClient httpClient;

    private Session? session;

    /// <summary>
    /// Creates a client for one Whisker account. Supply an <paramref name="httpClient"/> to
    /// control pooling and handlers; otherwise a shared instance is used.
    /// </summary>
    public LitterRobotClient(LitterRobotCredentials credentials, HttpClient? httpClient = null)
    {
        this.credentials = credentials;
        this.httpClient = httpClient ?? SharedHttpClient;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LitterRobotPet>> GetPetsAsync(
        CancellationToken cancellationToken = default) =>
        await GetPetsAsync(await GetSessionAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<WeightReading>> GetWeightHistoryAsync(
        LitterRobotPet pet,
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var readings = await GetWeightHistoryAsync(
            await GetSessionAsync(cancellationToken), pet, ResolveLimit(limit), cancellationToken);

        return Chronological(readings);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WeightReading>> GetAllWeightHistoryAsync(
        int limit = 0,
        CancellationToken cancellationToken = default)
    {
        var current = await GetSessionAsync(cancellationToken);
        var resolvedLimit = ResolveLimit(limit);

        var readings = new List<WeightReading>();
        foreach (var pet in await GetPetsAsync(current, cancellationToken))
            readings.AddRange(await GetWeightHistoryAsync(current, pet, resolvedLimit, cancellationToken));

        return Chronological(readings);
    }

    // The API returns newest-first; callers charting or deduping the data expect
    // chronological order.
    private static List<WeightReading> Chronological(List<WeightReading> readings)
    {
        readings.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return readings;
    }

    private static int ResolveLimit(int limit) =>
        limit > 0 ? limit : LitterRobotConstants.DefaultWeightHistoryLimit;

    // One sign-in serves every call until the token lapses.
    private async Task<Session> GetSessionAsync(CancellationToken cancellationToken)
    {
        if (session is { } cached && DateTimeOffset.UtcNow < cached.ExpiresAt)
            return cached;

        var idToken = await AuthenticateAsync(cancellationToken);
        var (userId, expiresAt) = ReadClaims(idToken);

        var current = new Session(idToken, userId, expiresAt);
        session = current;
        return current;
    }

    // Cognito USER_PASSWORD_AUTH; returns the id token used to authorize the
    // pet-profile API.
    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            AuthFlow = "USER_PASSWORD_AUTH",
            ClientId = LitterRobotConstants.CognitoClientId,
            AuthParameters = new
            {
                USERNAME = credentials.Username,
                PASSWORD = credentials.Password
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, LitterRobotConstants.CognitoEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/x-amz-json-1.1")
        };
        request.Headers.TryAddWithoutValidation("X-Amz-Target", LitterRobotConstants.CognitoInitiateAuthTarget);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw BuildCognitoException((int)response.StatusCode, body);

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("AuthenticationResult", out var result) ||
            !result.TryGetProperty("IdToken", out var idToken) ||
            idToken.GetString() is not { Length: > 0 } token)
            throw new LitterRobotApiException(
                (int)response.StatusCode, null,
                "Litter-Robot sign-in did not return an id token.", body);

        return token;
    }

    // The Whisker user id lives in the id token's "mid" claim. The token comes
    // straight from Cognito over TLS and is only used to call Whisker's own API,
    // so we read the claims without verifying the signature (as pylitterbot does).
    private static (string UserId, DateTimeOffset ExpiresAt) ReadClaims(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
            throw new LitterRobotApiException(0, null, "Litter-Robot id token is malformed.", idToken);

        using var document = JsonDocument.Parse(DecodeBase64Url(parts[1]));
        if (!document.RootElement.TryGetProperty(LitterRobotConstants.UserIdClaim, out var claim) ||
            claim.GetString() is not { Length: > 0 } userId)
            throw new LitterRobotApiException(
                0, null,
                $"Litter-Robot id token has no '{LitterRobotConstants.UserIdClaim}' claim.", string.Empty);

        // Without an expiry we simply re-authenticate on the next call.
        var expiresAt = document.RootElement.TryGetProperty(LitterRobotConstants.ExpiryClaim, out var expiry) &&
                        expiry.TryGetInt64(out var epochSeconds)
            ? DateTimeOffset.FromUnixTimeSeconds(epochSeconds)
                .AddSeconds(-LitterRobotConstants.TokenExpirySkewSeconds)
            : DateTimeOffset.UtcNow;

        return (userId, expiresAt);
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };
        return Convert.FromBase64String(padded);
    }

    private async Task<IReadOnlyList<LitterRobotPet>> GetPetsAsync(
        Session current,
        CancellationToken cancellationToken)
    {
        using var document = await QueryGraphQlAsync(
            current, PetsQuery, new { userId = current.UserId }, cancellationToken);

        var pets = new List<LitterRobotPet>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("getPetsByUser", out var petsElement) ||
            petsElement.ValueKind != JsonValueKind.Array)
            return pets;

        foreach (var pet in petsElement.EnumerateArray())
        {
            var petId = pet.TryGetProperty("petId", out var idElement) ? idElement.GetString() : null;
            var name = pet.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            if (!string.IsNullOrEmpty(petId) && !string.IsNullOrEmpty(name))
                pets.Add(new LitterRobotPet(petId!, name!));
        }

        return pets;
    }

    private async Task<List<WeightReading>> GetWeightHistoryAsync(
        Session current,
        LitterRobotPet pet,
        int limit,
        CancellationToken cancellationToken)
    {
        using var document = await QueryGraphQlAsync(
            current, WeightHistoryQuery, new { petId = pet.PetId, limit }, cancellationToken);

        var readings = new List<WeightReading>();
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("getWeightHistoryByPetId", out var history) ||
            history.ValueKind != JsonValueKind.Array)
            return readings;

        foreach (var entry in history.EnumerateArray())
        {
            if (!entry.TryGetProperty("timestamp", out var timestampElement) ||
                !TryParseTimestamp(timestampElement.GetString(), out var timestamp))
                continue;

            if (!entry.TryGetProperty("weight", out var weightElement) ||
                !TryGetWeight(weightElement, out var weight))
                continue;

            readings.Add(new WeightReading(pet.PetId, pet.Name, timestamp, weight));
        }

        return readings;
    }

    private async Task<JsonDocument> QueryGraphQlAsync(
        Session current,
        string query,
        object variables,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });

        using var request = new HttpRequestMessage(HttpMethod.Post, LitterRobotConstants.PetProfileEndpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.IdToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw BuildGraphQlException((int)response.StatusCode, body);

        var document = JsonDocument.Parse(body);

        // GraphQL reports failures in an "errors" array with a 200 status.
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;
            document.Dispose();
            throw new LitterRobotApiException((int)response.StatusCode, "GraphQLError", message, body);
        }

        return document;
    }

    // Mirrors pylitterbot's to_timestamp: the API omits an offset (or uses "Z"),
    // and the value is always UTC.
    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);
    }

    private static bool TryGetWeight(JsonElement element, out double weight)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                weight = element.GetDouble();
                return true;
            case JsonValueKind.String:
                return double.TryParse(element.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out weight);
            default:
                weight = 0;
                return false;
        }
    }

    // Cognito errors: { "__type": "NotAuthorizedException", "message": "..." }
    private static LitterRobotApiException BuildCognitoException(int statusCode, string body)
    {
        string? errorType = null;
        string? message = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("__type", out var typeElement))
                errorType = typeElement.GetString();
            if (document.RootElement.TryGetProperty("message", out var messageElement))
                message = messageElement.GetString();
        }
        catch (JsonException)
        {
            // Non-JSON body; fall back to the status code only.
        }

        // Surface the two failures worth acting on with guidance rather than the
        // raw Cognito wording.
        var friendly = errorType switch
        {
            "NotAuthorizedException" => "Litter-Robot rejected the credentials.",
            "UserNotFoundException" => "No Litter-Robot account found for that email address.",
            _ => message
        };

        return new LitterRobotApiException(statusCode, errorType, friendly, body);
    }

    private static LitterRobotApiException BuildGraphQlException(int statusCode, string body)
    {
        string? message = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var messageElement))
                message = messageElement.GetString();
        }
        catch (JsonException)
        {
            // Non-JSON body; fall back to the status code only.
        }

        return new LitterRobotApiException(statusCode, null, message, body);
    }

    private readonly record struct Session(string IdToken, string UserId, DateTimeOffset ExpiresAt);
}
