using CommandLine;

public class Options
{
    [Option('a', "account", Required = true, HelpText = $"CosmosDB account URL to connect to. Typically https://<accountname>.documents.azure.com:443/")]
    public required string CosmosAccountUrl { get; set; }
    [Option('k', "key", Required = true, HelpText = "CosmosDB account key to use.")]
    public required string CosmosAccountKey { get; set; }
    [Option('d', "database", Required = true, HelpText = "CosmosDB database to query in the account.")]
    public required string CosmosDatabase { get; set; }
    [Option('c', "container", Required = true, HelpText = "CosmosDB container to query in the database.")]
    public required string CosmosContainer { get; set; }
    [Option('q', "query", Required = true, HelpText = "CosmosDB query used to select documents to patch. Use the token ##TS## for the current UTC date and time as a Unix seconds timestamp, or ##UTCNOW## for the current UTC date and time in ISO8601 format.")]
    public required string Query { get; set; }
    [Option('p', "patch", Required = true, HelpText = "JSON Patch specification for the patch to apply to each document.")]
    public required string JsonPatch { get; set; }
    [Option('g', "gateway", Required = false, Default = false, HelpText = "Enable gateway mode on connections to CosmosDB rather than the default direct mode.")]
    public bool GatewayMode { get; set; }
}
