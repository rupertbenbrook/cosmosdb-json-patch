using System.Diagnostics;
using System.Runtime.CompilerServices;
using CommandLine;
using CommandLine.Text;
using Json.Patch;
using Microsoft.Azure.Cosmos;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CosmosJsonPatch;

public class Program(Options options)
{
    private long _queried;
    private long _patched;
    private long _unchanged;
    private long _failed;

    public static async Task Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        var run = Task.CompletedTask;
        Console.CancelKeyPress += (_, e) =>
        {
            Console.WriteLine("Shutdown requested...");
            cts.Cancel();
            e.Cancel = true;
            // ReSharper disable once MethodSupportsCancellation
            // ReSharper disable once AccessToModifiedClosure
            run.Wait();
        };
        run = Parser.Default.ParseArguments<Options>(args)
            .WithParsedAsync(options => new Program(options).RunAsync(cts.Token));
        await run;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        WriteRunHeader();
        var stopwatch = Stopwatch.StartNew();
        var connect = await ConnectToCosmos(cancellationToken);
        if (connect == null)
        {
            return;
        }
        var container = connect.Value.Item1;
        var partitionKeyPath = connect.Value.Item2;

        var queryText = ParseQueryText();
        if (queryText == null)
        {
            return;
        }

        var jsonPatch = ParseJsonPatch();
        if (jsonPatch == null)
        {
            return;
        }

        await foreach (var document in QueryDocuments(container, queryText, cancellationToken))
        {
            await PatchDocument(container, partitionKeyPath, document, jsonPatch, cancellationToken);
        }
        stopwatch.Stop();

        Console.WriteLine($"Completed in {stopwatch.Elapsed} at {_patched / stopwatch.Elapsed.TotalSeconds:F2} documents patched/sec.");
        Console.WriteLine($"{_queried} documents found, {_patched} patched, {_unchanged} unchanged, and {_failed} failed.");
    }

    private void WriteRunHeader()
    {
        var mode = options.GatewayMode ? "Gateway" : "Direct";
        Console.WriteLine(HeadingInfo.Default.ToString());
        Console.WriteLine(CopyrightInfo.Default.ToString());
        Console.WriteLine();
        Console.WriteLine($"Account URL: {options.CosmosAccountUrl}");
        Console.WriteLine($"       Mode: {mode}");
        Console.WriteLine($"   Database: {options.CosmosDatabase}");
        Console.WriteLine($"  Container: {options.CosmosContainer}");
        Console.WriteLine($"      Query: {ParseQueryText()}");
        Console.WriteLine($" JSON Patch: {options.JsonPatch}");
        Console.WriteLine();
    }

    private async Task<(Container, string)?> ConnectToCosmos(CancellationToken cancellationToken)
    {
        Console.WriteLine("Connecting to CosmosDB...");
        CosmosClient client = new(options.CosmosAccountUrl,
            options.CosmosAccountKey,
            new CosmosClientOptions()
            {
                ConnectionMode = options.GatewayMode ? ConnectionMode.Gateway : ConnectionMode.Direct,
                ApplicationName = "CosmosJsonPatch",
                UseSystemTextJsonSerializerWithOptions = JsonSerializerOptions.Default
            }
        );
        var container = client.GetContainer(options.CosmosDatabase, options.CosmosContainer);
        try
        {
            var containerPropertiesResponse = await container.ReadContainerAsync(cancellationToken: cancellationToken);
            if (containerPropertiesResponse.StatusCode != System.Net.HttpStatusCode.OK)
            {
                Console.WriteLine($"ERROR: Failed to connect to CosmosDB. {containerPropertiesResponse.StatusCode}");
                return null;
            }
            return (container, containerPropertiesResponse.Resource.PartitionKeyPath);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: Failed to connect to CosmosDB. {ex.Message}");
            return null;
        }
    }

    public string? ParseQueryText()
    {
        return options.Query
            .Replace("##TS##", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
            .Replace("##UTCNOW##", DateTimeOffset.UtcNow.ToString("O"));
    }

    public JsonPatch? ParseJsonPatch()
    {
        try
        {
            var jsonPatch = JsonSerializer.Deserialize<JsonPatch>(options.JsonPatch);
            if (jsonPatch == null)
            {
                Console.WriteLine("Failed to parse JSON Patch.");
                return null;
            }
            return jsonPatch;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse JSON Patch. {ex.Message}");
            return null;
        }
    }

    private async IAsyncEnumerable<JsonNode> QueryDocuments(Container container, string queryText, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Console.WriteLine("Querying for documents...");
        var query = new QueryDefinition(queryText);
        using var feed = container.GetItemQueryStreamIterator(query);
        while (feed.HasMoreResults && !cancellationToken.IsCancellationRequested)
        {
            JsonNode? results;
            try
            {
                var response = await feed.ReadNextAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine(
                        $"ERROR: Failed to read query results. {response.StatusCode} {response.ErrorMessage}");
                    yield break;
                }

                results = await JsonNode.ParseAsync(response.Content, cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR: Failed to read query results. {ex.Message}");
                yield break;
            }

            var documents = results?.Root["Documents"];
            if (documents == null || documents.AsArray().Count == 0)
            {
                continue;
            }
            foreach (var document in documents.AsArray())
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                if (document == null)
                {
                    continue;
                }
                _queried++;
                yield return document;
            }
        }
    }

    private async Task PatchDocument(Container container, string partitionKeyPath, JsonNode document,
        JsonPatch jsonPatch, CancellationToken cancellationToken)
    {
        var id = document["id"]?.ToString() ?? "";
        var paths = partitionKeyPath.Split('/');
        var key = document;
        foreach (var path in paths)
        {
            key = document[path];
        }
        var partitionKey = key?.ToString();

        Console.Write($"Patching document \"{id}\"/\"{partitionKey ?? "<null>"}\"...");
        try
        {
            var result = jsonPatch.Apply(document);
            if (!result.IsSuccess || result.Result == null)
            {
                Console.WriteLine("failed");
                var error = result.IsSuccess ? "Patched document is null." : result.Error;
                Console.WriteLine($"ERROR: {error}");
                _failed++;
                return;
            }

            if (JsonNode.DeepEquals(document, result.Result))
            {
                Console.WriteLine("unchanged");
                _unchanged++;
                return;
            }

            await container.ReplaceItemAsync(
                result.Result,
                id,
                partitionKey == null ? null : new PartitionKey(partitionKey),
                cancellationToken: cancellationToken);
            Console.WriteLine("patched");
            _patched++;
        }
        catch (OperationCanceledException)
        {
            // Ignore
        }
        catch (Exception ex)
        {
            Console.WriteLine("failed");
            Console.WriteLine($"ERROR: {ex.Message}");
            _failed++;
        }
    }
}
