namespace LitterRobotApi.Exceptions;

/// <summary>Thrown when the Whisker sign-in or pet-profile API returns a failure.</summary>
public class LitterRobotApiException(
    int statusCode,
    string? errorType,
    string? apiMessage,
    string responseBody)
    : Exception(apiMessage ?? $"Litter-Robot API returned {statusCode}.")
{
    /// <summary>HTTP status code of the failed response, or 0 if the failure was local.</summary>
    public int StatusCode { get; } = statusCode;

    /// <summary>Cognito's <c>__type</c> value, or "GraphQLError", when one was reported.</summary>
    public string? ErrorType { get; } = errorType;

    /// <summary>The message the API returned, if it supplied one.</summary>
    public string? ApiMessage { get; } = apiMessage;

    /// <summary>The raw response body, kept for diagnostics.</summary>
    public string ResponseBody { get; } = responseBody;
}
