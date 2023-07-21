#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace Mochineko.ChatGPT_API
{
    /// <summary>
    /// Binds ChatGPT chat completion API.
    /// </summary>
    public sealed class ChatCompletionAPIConnection
    {
        private readonly string apiKey;
        private readonly IChatMemory chatMemory;
        private readonly HttpClient httpClient;

        private const string ChatCompletionEndPoint = "https://api.openai.com/v1/chat/completions";

        /// <summary>
        /// Create an instance of ChatGPT chat completion API connection.
        /// https://platform.openai.com/docs/api-reference/chat/create
        /// </summary>
        /// <param name="apiKey">API key generated by OpenAI</param>
        /// <param name="chatMemory"></param>
        /// <param name="prompt"></param>
        /// <param name="httpClient"></param>
        /// <exception cref="ArgumentNullException">API Key must be set</exception>
        public ChatCompletionAPIConnection(
            string apiKey,
            IChatMemory? chatMemory = null,
            string? prompt = null,
            HttpClient? httpClient = null)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentNullException(nameof(apiKey));
            }

            this.apiKey = apiKey;
            this.chatMemory = chatMemory ?? new SimpleChatMemory();

            if (!string.IsNullOrEmpty(prompt))
            {
                this.chatMemory.AddMessageAsync(
                    new Message(Role.System, prompt),
                    CancellationToken.None);
            }

            this.httpClient = httpClient ?? HttpClientPool.PooledClient;
        }

        /// <summary>
        /// Completes chat though ChatGPT chat completion API.
        /// https://platform.openai.com/docs/api-reference/chat/create
        /// </summary>
        /// <param name="content">Message content to send ChatGPT API</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="model"></param>
        /// <param name="functions"></param>
        /// <param name="functionCallString"></param>
        /// <param name="functionCallSpecifying"></param>
        /// <param name="temperature"></param>
        /// <param name="topP"></param>
        /// <param name="n"></param>
        /// <param name="stream"></param>
        /// <param name="stop"></param>
        /// <param name="maxTokens"></param>
        /// <param name="presencePenalty"></param>
        /// <param name="frequencyPenalty"></param>
        /// <param name="logitBias"></param>
        /// <param name="user"></param>
        /// <param name="verbose"></param>
        /// <returns>Response from ChatGPT chat completion API.</returns>
        /// <exception cref="Exception">System exceptions</exception>
        /// <exception cref="ArgumentException"></exception>
        /// <exception cref="APIErrorException">API error response</exception>
        /// <exception cref="HttpRequestException">Network error</exception>
        /// <exception cref="TaskCanceledException">Cancellation or timeout</exception>
        /// <exception cref="JsonSerializationException">JSON error</exception>
        public async Task<ChatCompletionResponseBody> CompleteChatAsync(
            string content,
            CancellationToken cancellationToken,
            Model model = Model.Turbo,
            IReadOnlyList<Function>? functions = null,
            string? functionCallString = null,
            FunctionCallSpecifying? functionCallSpecifying = null,
            float? temperature = null,
            float? topP = null,
            uint? n = null,
            bool? stream = null,
            string[]? stop = null,
            int? maxTokens = null,
            float? presencePenalty = null,
            float? frequencyPenalty = null,
            Dictionary<int, int>? logitBias = null,
            string? user = null,
            bool verbose = false)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Record user message
            await chatMemory.AddMessageAsync(
                new Message(Role.User, content),
                cancellationToken);

            // Create request body
            var requestBody = new ChatCompletionRequestBody(
                model.ToText(),
                chatMemory.Messages,
                functions,
                functionCallString,
                functionCallSpecifying,
                temperature,
                topP,
                n,
                stream,
                stop,
                maxTokens,
                presencePenalty,
                frequencyPenalty,
                logitBias,
                user);

            var requestJson = requestBody.ToJson();
            if (verbose)
            {
                Debug.Log($"[ChatGPT_API] Request content:\n{requestJson}]");
            }

            // Build request message
            using var requestMessage = new HttpRequestMessage(
                HttpMethod.Post, ChatCompletionEndPoint);
            requestMessage.Headers
                .Add("Authorization", $"Bearer {apiKey}");

            var requestContent = new StringContent(
                content: requestJson,
                encoding: System.Text.Encoding.UTF8,
                mediaType: "application/json");

            requestMessage.Content = requestContent;

            // Post request and receive response
            // NOTE: Can throw exceptions
            using var responseMessage = await httpClient
                .SendAsync(requestMessage, cancellationToken);

            if (responseMessage == null)
            {
                throw new Exception($"[ChatGPT_API] HttpResponseMessage is null.");
            }

            if (responseMessage.Content == null)
            {
                throw new Exception($"[ChatGPT_API] HttpResponseMessage.Content is null.");
            }

            var responseJson = await responseMessage.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(responseJson))
            {
                throw new Exception($"[ChatGPT_API] Response JSON is null or empty.");
            }

            if (verbose)
            {
                Debug.Log(
                    $"[ChatGPT_API] Status code: {(int)responseMessage.StatusCode}, {responseMessage.StatusCode}");
                Debug.Log($"[ChatGPT_API] Response body:\n{responseJson}");
            }

            // Succeeded
            if (responseMessage.IsSuccessStatusCode)
            {
                var responseBody = ChatCompletionResponseBody.FromJson(responseJson);
                if (responseBody == null)
                {
                    throw new Exception($"[ChatGPT_API] Response body is null.");
                }

                if (responseBody.Choices.Length == 0)
                {
                    throw new Exception($"[ChatGPT_API] Not found any choices in response body:{responseJson}.");
                }

                // Record assistant messages
                foreach (var choice in responseBody.Choices)
                {
                    // NOTE: Fill message content with empty string because messages in request parameter must have content field. 
                    if (choice.Message.Content is null)
                    {
                        choice.Message.Content = string.Empty;
                    }
                    
                    await chatMemory.AddMessageAsync(
                        choice.Message,
                        cancellationToken);
                }

                return responseBody;
            }
            // Failed
            else
            {
                try
                {
                    responseMessage.EnsureSuccessStatusCode();
                }
                catch (Exception e)
                {
                    throw new APIErrorException(responseJson, responseMessage.StatusCode, e);
                }

                throw new Exception(
                    $"[ChatGPT_API] System error with status code:{responseMessage.StatusCode}, message:{responseJson}");
            }
        }

        /// <summary>
        /// Completes chat as <see cref="IAsyncEnumerable{T}"/> of <see cref="ChatCompletionStreamResponseChunk"/> though server-sent event of ChatGPT chat completion API.
        /// https://platform.openai.com/docs/api-reference/chat/create
        /// </summary>
        /// <param name="content">Message content to send ChatGPT API</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <param name="model"></param>
        /// <param name="functions"></param>
        /// <param name="functionCallString"></param>
        /// <param name="functionCallSpecifying"></param>
        /// <param name="temperature"></param>
        /// <param name="topP"></param>
        /// <param name="n"></param>
        /// <param name="stop"></param>
        /// <param name="maxTokens"></param>
        /// <param name="presencePenalty"></param>
        /// <param name="frequencyPenalty"></param>
        /// <param name="logitBias"></param>
        /// <param name="user"></param>
        /// <param name="verbose"></param>
        /// <returns>Response async enumerable of chunk from ChatGPT chat completion API.</returns>
        /// <exception cref="Exception">System exceptions</exception>
        /// <exception cref="APIErrorException">API error response</exception>
        /// <exception cref="HttpRequestException">Network error</exception>
        /// <exception cref="TaskCanceledException">Cancellation or timeout</exception>
        /// <exception cref="JsonSerializationException">JSON error</exception>
        public async Task<IAsyncEnumerable<ChatCompletionStreamResponseChunk>>
            CompleteChatAsStreamAsync(
                string content,
                CancellationToken cancellationToken,
                Model model = Model.Turbo,
                IReadOnlyList<Function>? functions = null,
                string? functionCallString = null,
                FunctionCallSpecifying? functionCallSpecifying = null,
                float? temperature = null,
                float? topP = null,
                uint? n = null,
                string[]? stop = null,
                int? maxTokens = null,
                float? presencePenalty = null,
                float? frequencyPenalty = null,
                Dictionary<int, int>? logitBias = null,
                string? user = null,
                bool verbose = false)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (functionCallString != null && functionCallSpecifying != null)
            {
                throw new ArgumentException(
                    $"You can use only one of {nameof(functionCallString)} and {nameof(functionCallSpecifying)}.");
            }

            // Record user message
            await chatMemory.AddMessageAsync(
                new Message(Role.User, content),
                cancellationToken);

            // Create request body
            var requestBody = new ChatCompletionRequestBody(
                model.ToText(),
                chatMemory.Messages,
                functions,
                functionCallString,
                functionCallSpecifying,
                temperature,
                topP,
                n,
                stream: true, // NOTE: Always true
                stop,
                maxTokens,
                presencePenalty,
                frequencyPenalty,
                logitBias,
                user);

            var requestJson = requestBody.ToJson();
            if (verbose)
            {
                Debug.Log($"[ChatGPT_API] Request body:\n{requestJson}");
            }

            // Build request message
            using var requestMessage = new HttpRequestMessage(
                HttpMethod.Post, ChatCompletionEndPoint);
            requestMessage.Headers
                .Add("Authorization", $"Bearer {apiKey}");

            using var requestContent = new StringContent(
                content: requestJson,
                encoding: System.Text.Encoding.UTF8,
                mediaType: "application/json");

            requestMessage.Content = requestContent;

            // Post request and receive response
            // NOTE: Can throw exceptions
            var responseMessage = await httpClient
                .SendAsync(
                    requestMessage,
                    HttpCompletionOption.ResponseHeadersRead, // NOTE: To read as stream immediately
                    cancellationToken);

            if (responseMessage == null)
            {
                throw new Exception($"[ChatGPT_API] HttpResponseMessage is null.");
            }

            if (responseMessage.Content == null)
            {
                throw new Exception($"[ChatGPT_API] HttpResponseMessage.Content is null.");
            }

            if (verbose)
            {
                Debug.Log(
                    $"[ChatGPT_API] Status code: {(int)responseMessage.StatusCode}, {responseMessage.StatusCode}");
            }

            // Succeeded
            if (responseMessage.IsSuccessStatusCode)
            {
                var stream = await responseMessage.Content.ReadAsStreamAsync();

                // Convert stream to async enumerable
                return ReadChunkAsAsyncEnumerable(stream, cancellationToken, responseMessage, verbose);
            }
            // Failed
            else
            {
                var responseJson = await responseMessage.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(responseJson))
                {
                    throw new Exception($"[ChatGPT_API] Response JSON is null or empty.");
                }

                try
                {
                    responseMessage.EnsureSuccessStatusCode();
                }
                catch (Exception e)
                {
                    throw new APIErrorException(responseJson, responseMessage.StatusCode, e);
                }

                throw new Exception(
                    $"[ChatGPT_API] System error with status code:{responseMessage.StatusCode}, message:{responseJson}");
            }
        }

        private static async IAsyncEnumerable<ChatCompletionStreamResponseChunk> ReadChunkAsAsyncEnumerable(
            Stream stream,
            [EnumeratorCancellation] CancellationToken cancellationToken,
            IDisposable response,
            bool verbose)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                while (!reader.EndOfStream || !cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (verbose)
                    {
                        Debug.Log($"[ChatGPT_API] Response delta : {line}");
                    }
                    
                    if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var formatted = line.TrimStart("data: ".ToCharArray());
                    if (string.IsNullOrEmpty(formatted))
                    {
                        continue;
                    }

                    // Finished
                    if (formatted == "[DONE]")
                    {
                        break;
                    }
                    
                    var chunk = ChatCompletionStreamResponseChunk.FromJson(formatted);
                    if (chunk is null)
                    {
                        throw new Exception($"[ChatGPT_API] Response delta JSON is null or empty.");
                    }

                    yield return chunk;
                }
            }
            finally
            {
                await stream.DisposeAsync();
                response.Dispose();
            }
        }
    }
}