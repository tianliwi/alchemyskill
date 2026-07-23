namespace AlchemyProxy.Infrastructure;

public sealed class AlchemyOptions
{
    public const string SectionName = "Alchemy";

    public string BaseUrl { get; init; } = "https://alchemy.microsoft.com/api/v2/";

    public string AppId { get; init; } = "servicenowvirtualagent";

    public int TimeoutSeconds { get; init; } = 30;
}

public sealed class LocalStorageOptions
{
    public const string SectionName = "LocalStorage";

    public string RootPath { get; init; } = ".local";

    public int SessionTtlHours { get; init; } = 24;
}

public sealed class ApiException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string Code { get; } = code;
}
