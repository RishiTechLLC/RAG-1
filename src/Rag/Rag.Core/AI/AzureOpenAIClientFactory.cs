using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using OpenAI;

namespace Rag.Core.AI;

/// <summary>
/// Builds an <see cref="OpenAIClient"/> that targets the Azure OpenAI "v1" endpoint
/// (https://{resource}.openai.azure.com/openai/v1/). Uses an API key when configured,
/// otherwise Entra ID through <see cref="TokenCredential"/> (managed identity in Azure, az login locally).
/// </summary>
public static class AzureOpenAIClientFactory
{
    private const string CognitiveServicesScope = "https://cognitiveservices.azure.com/.default";

    public static OpenAIClient Create(AzureOpenAIOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
        }

        var clientOptions = new OpenAIClientOptions { Endpoint = BuildV1Endpoint(options.Endpoint) };

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
        }

#pragma warning disable OPENAI001 // Token-credential auth is the documented way to use Entra ID with the v1 endpoint.
        return new OpenAIClient(new BearerTokenPolicy(credential, CognitiveServicesScope), clientOptions);
#pragma warning restore OPENAI001
    }

    internal static Uri BuildV1Endpoint(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        return trimmed.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase)
            ? new Uri(trimmed + "/")
            : new Uri(trimmed + "/openai/v1/");
    }
}
