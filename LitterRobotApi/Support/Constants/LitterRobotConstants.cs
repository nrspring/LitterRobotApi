namespace LitterRobotApi.Support.Constants;

internal static class LitterRobotConstants
{
    // Whisker's public Cognito app client. The pool allows USER_PASSWORD_AUTH, so
    // a plain InitiateAuth is enough and we can skip the SRP handshake that
    // pylitterbot performs via pycognito.
    public const string CognitoEndpoint = "https://cognito-idp.us-east-1.amazonaws.com/";
    public const string CognitoInitiateAuthTarget = "AWSCognitoIdentityProviderService.InitiateAuth";
    public const string CognitoClientId = "4552ujeu3aic90nf8qn53levmn";

    // The id token's "mid" claim carries the Whisker user id that the pet-profile
    // API keys off of.
    public const string UserIdClaim = "mid";
    public const string ExpiryClaim = "exp";

    public const string PetProfileEndpoint = "https://pet-profile.iothings.site/graphql/";

    public const int DefaultWeightHistoryLimit = 100;

    // Re-authenticate slightly before the token actually lapses.
    public const int TokenExpirySkewSeconds = 60;
}
