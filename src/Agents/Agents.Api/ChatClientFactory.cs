using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Agents.Api;

/// <summary>Creates clients for an Azure OpenAI resource via its v1 endpoint (API key, or Entra ID when no key is set).</summary>
public static class ChatClientFactory
{
    public static OpenAIClient CreateOpenAIClient(AzureOpenAIOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("AzureOpenAI:Endpoint is not configured.");
        }

        var endpoint = options.Endpoint.TrimEnd('/');
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase) ? endpoint + "/" : endpoint + "/openai/v1/"),
        };

#pragma warning disable OPENAI001 // Token-credential auth for Entra ID.
        return string.IsNullOrWhiteSpace(options.ApiKey)
            ? new OpenAIClient(new BearerTokenPolicy(credential, "https://cognitiveservices.azure.com/.default"), clientOptions)
            : new OpenAIClient(new ApiKeyCredential(options.ApiKey), clientOptions);
#pragma warning restore OPENAI001
    }

    public static IChatClient Create(AzureOpenAIOptions options, TokenCredential credential) =>
        CreateOpenAIClient(options, credential).GetChatClient(options.ChatDeployment).AsIChatClient();
}
