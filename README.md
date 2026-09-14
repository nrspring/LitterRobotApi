# LitterRobotApi

An unofficial .NET client for the Litter-Robot/Whisker API — Cognito sign-in and pet weight
history via their GraphQL API, ported from [pylitterbot](https://github.com/natekspencer/pylitterbot).

Targets `net8.0` and `net10.0`, with no package dependencies.

## Install

```
dotnet add package LitterRobotApi
```

## Usage

```csharp
using LitterRobotApi.Implementations;
using LitterRobotApi.Models;

var client = new LitterRobotClient(new LitterRobotCredentials(username, password));

foreach (var pet in await client.GetPetsAsync())
    Console.WriteLine(pet.Name);

// Every pet's readings, oldest first, 100 per pet by default.
foreach (var reading in await client.GetAllWeightHistoryAsync())
    Console.WriteLine($"{reading.PetName} {reading.Timestamp:d} {reading.WeightLbs} lbs");
```

The client signs in on first use and reuses the token until it expires. Pass your own
`HttpClient` to the constructor to control pooling and handlers:

```csharp
var client = new LitterRobotClient(credentials, httpClient);
```

Where credentials come from is up to the caller — environment variables, user secrets,
a vault. The library never reads configuration itself.

Failures from either API surface as `LitterRobotApiException`, which carries the status
code, Cognito error type, and raw response body.

## Caveats

Whisker publishes no public API. The endpoints here were derived from `pylitterbot` and can
change without notice — treat a working version as a snapshot, not a contract. Use it with
your own account only.

## Releasing

CI builds every push and pull request on `main`. Pushing a `v*` tag builds, packs, and
publishes to NuGet with the version taken from the tag:

```
git tag v0.1.0 && git push origin v0.1.0
```

This needs a `NUGET_API_KEY` repository secret holding a nuget.org API key scoped to
`LitterRobotApi`.

## License

MIT
