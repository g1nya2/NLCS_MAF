using System.ClientModel;
using System.Collections.Concurrent;
using Azure;
using Azure.AI.Translation.Document;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Agents.AI.DevUI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

#pragma warning disable OPENAI001

var builder = WebApplication.CreateBuilder(args);

// [STEP 1] LLM chat client를 생성하고 DI에 등록하세요.

// Blob client
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["Azure:Storage:ConnectionString"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Storage:ConnectionString");

    return new BlobServiceClient(connectionString);
});

// [STEP 2] DocumentTranslationClient를 DI에 등록하세요.

// [STEP 3] 문서번역 오케스트레이션용 단일 에이전트를 등록하세요.

builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

var app = builder.Build();

var jobs = new ConcurrentDictionary<string, Translation5PassJob>();

using (var scope = app.Services.CreateScope())
{
    var blobServiceClient = scope.ServiceProvider.GetRequiredService<BlobServiceClient>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    await EnsureContainersAsync(blobServiceClient, config);
}

app.MapOpenAIResponses();
app.MapOpenAIConversations();

if (!builder.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
else
{
    app.MapDevUI();
}

app.MapGet("/", () => Results.Ok("Document Translation Agent is running."));

app.MapPost("/api/translate/upload", async (
    HttpRequest request,
    BlobServiceClient blobServiceClient,
    IConfiguration config) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "multipart/form-data 형식이어야 합니다." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "업로드된 파일이 없습니다." });
    }

    if (!string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "PDF 파일만 업로드할 수 있습니다." });
    }

    var sourceContainerName = config["Azure:Storage:SourceContainer"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Storage:SourceContainer");

    var sourceContainer = blobServiceClient.GetBlobContainerClient(sourceContainerName);

    var blobName = $"{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}";
    var blobClient = sourceContainer.GetBlobClient(blobName);

    await using var stream = file.OpenReadStream();
    await blobClient.UploadAsync(stream, overwrite: true);

    return Results.Json(new
    {
        fileName = file.FileName,
        blobName,
        container = sourceContainerName
    });
});

// [STEP 4] 5-pass 번역 시작 API를 구현하세요.

// [STEP 5] 번역 상태 조회 API를 구현하세요.

// [STEP 6] 최종 결과 조회 API를 구현하세요.

await app.RunAsync();

static async Task EnsureContainersAsync(BlobServiceClient blobServiceClient, IConfiguration config)
{
    var names = new[]
    {
        config["Azure:Storage:SourceContainer"],
        config["Azure:Storage:TargetKo1Container"],
        config["Azure:Storage:TargetEn2Container"],
        config["Azure:Storage:TargetKo3Container"],
        config["Azure:Storage:TargetEn4Container"],
        config["Azure:Storage:TargetKo5Container"]
    };

    foreach (var name in names)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            continue;
        }

        var container = blobServiceClient.GetBlobContainerClient(name);
        await container.CreateIfNotExistsAsync();
    }
}

static Uri GenerateBlobSasUri(BlobClient blobClient, TimeSpan duration)
{
    if (!blobClient.CanGenerateSasUri)
    {
        throw new InvalidOperationException("Blob SAS URI를 생성할 수 없습니다. Storage connection string 또는 shared key 방식인지 확인하세요.");
    }

    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = blobClient.BlobContainerName,
        BlobName = blobClient.Name,
        Resource = "b",
        ExpiresOn = DateTimeOffset.UtcNow.Add(duration)
    };

    sasBuilder.SetPermissions(BlobSasPermissions.Read);

    return blobClient.GenerateSasUri(sasBuilder);
}

static Uri GenerateSourceContainerSasUri(BlobContainerClient containerClient, TimeSpan duration)
{
    if (!containerClient.CanGenerateSasUri)
    {
        throw new InvalidOperationException(
            "Source container SAS URI를 생성할 수 없습니다. Storage connection string 또는 shared key 방식인지 확인하세요.");
    }

    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = containerClient.Name,
        Resource = "c",
        ExpiresOn = DateTimeOffset.UtcNow.Add(duration)
    };

    sasBuilder.SetPermissions(
        BlobContainerSasPermissions.Read |
        BlobContainerSasPermissions.List);

    return containerClient.GenerateSasUri(sasBuilder);
}

static Uri GenerateTargetContainerSasUri(BlobContainerClient containerClient, TimeSpan duration)
{
    if (!containerClient.CanGenerateSasUri)
    {
        throw new InvalidOperationException(
            "Target container SAS URI를 생성할 수 없습니다. Storage connection string 또는 shared key 방식인지 확인하세요.");
    }

    var sasBuilder = new BlobSasBuilder
    {
        BlobContainerName = containerClient.Name,
        Resource = "c",
        ExpiresOn = DateTimeOffset.UtcNow.Add(duration)
    };

    sasBuilder.SetPermissions(
        BlobContainerSasPermissions.Write |
        BlobContainerSasPermissions.List);

    return containerClient.GenerateSasUri(sasBuilder);
}

// [STEP 7] 단일 번역 pass를 실행하는 헬퍼를 구현하세요.

public sealed class StartFivePassRequest
{
    public string BlobName { get; set; } = "";
    public string? FileName { get; set; }
}

public sealed class Translation5PassJob
{
    public string JobId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string BlobName { get; set; } = "";

    public string Status { get; set; } = "queued";
    public string Step { get; set; } = "queued";
    public string Message { get; set; } = "";

    public int CurrentPass { get; set; }
    public int TotalPasses { get; set; } = 5;

    public string SourceContainer { get; set; } = "";
    public string FinalOutputContainer { get; set; } = "";

    public string? Pass1OperationId { get; set; }
    public string? Pass2OperationId { get; set; }
    public string? Pass3OperationId { get; set; }
    public string? Pass4OperationId { get; set; }
    public string? Pass5OperationId { get; set; }

    public string? Pass1Status { get; set; }
    public string? Pass2Status { get; set; }
    public string? Pass3Status { get; set; }
    public string? Pass4Status { get; set; }
    public string? Pass5Status { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? Error { get; set; }
}

public class ChatClientFactory
{
    public static async Task<IChatClient> CreateChatClientAsync(IConfiguration config, IEnumerable<string> args)
    {
        var provider = config["LlmProvider"];

        foreach (var arg in args)
        {
            var index = args.ToList().IndexOf(arg);
            switch (arg)
            {
                case "--provider":
                    provider = args.ToList()[index + 1];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidOperationException("Missing configuration: LlmProvider");
        }

        var normalizedProvider = provider.Trim() switch
        {
            var p when p.Equals("Ollama", StringComparison.OrdinalIgnoreCase) => "Ollama",
            var p when p.Equals("GitHubModels", StringComparison.OrdinalIgnoreCase) => "GitHubModels",
            var p when p.Equals("AzureOpenAI", StringComparison.OrdinalIgnoreCase)
                || p.Equals("AzureOpenai", StringComparison.OrdinalIgnoreCase) => "AzureOpenAI",
            _ => provider
        };

        // [STEP 8] provider 이름에 따라 적절한 chat client를 반환하세요.

        return chatClient;
    }

    private static async Task<IChatClient> CreateOllamaChatClientAsync(IConfiguration config, string provider)
    {
        var ollama = config.GetSection("Ollama");
        var endpoint = ollama["Endpoint"] ?? throw new InvalidOperationException("Missing configuration: Ollama:Endpoint");
        var model = ollama["Model"] ?? throw new InvalidOperationException("Missing configuration: Ollama:Model");

        Console.WriteLine();
        Console.WriteLine($"\tUsing {provider}: {model}");
        Console.WriteLine();

        var client = new OllamaApiClient(endpoint, model);

        var pulls = client.PullModelAsync(model);
        var status = default(string);
        await foreach (var pull in pulls)
        {
            if (status == pull?.Status)
            {
                continue;
            }

            Console.WriteLine($"Pulling model '{model}': {pull?.Status}");
            status = pull?.Status;
        }

        return client;
    }

    private static async Task<IChatClient> CreateGitHubModelsChatClientAsync(IConfiguration config, string provider)
    {
        var github = config.GetSection("GitHub");
        var endpoint = github["Endpoint"] ?? throw new InvalidOperationException("Missing configuration: GitHub:Endpoint");
        var token = github["Token"] ?? throw new InvalidOperationException("Missing configuration: GitHub:Token");
        var model = github["Model"] ?? throw new InvalidOperationException("Missing configuration: GitHub:Model");

        Console.WriteLine();
        Console.WriteLine($"\tUsing {provider}: {model}");
        Console.WriteLine();

        var credential = new ApiKeyCredential(token);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint)
        };

        var client = new OpenAIClient(credential, options);
        return await Task.FromResult(client.GetChatClient(model).AsIChatClient());
    }

    private static async Task<IChatClient> CreateAzureOpenAIChatClientAsync(IConfiguration config, string provider)
    {
        var azure = config.GetSection("Azure:OpenAI");
        var endpoint = azure["Endpoint"] ?? throw new InvalidOperationException("Missing configuration: Azure:OpenAI:Endpoint");
        var apiKey = azure["ApiKey"] ?? throw new InvalidOperationException("Missing configuration: Azure:OpenAI:ApiKey");
        var deploymentName = azure["DeploymentName"] ?? throw new InvalidOperationException("Missing configuration: Azure:OpenAI:DeploymentName");

        Console.WriteLine();
        Console.WriteLine($"\tUsing {provider}: {deploymentName}");
        Console.WriteLine();

        var credential = new ApiKeyCredential(apiKey);
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri($"{endpoint.TrimEnd('/')}/openai/v1/")
        };

        var client = new OpenAIClient(credential, options);
        return await Task.FromResult(client.GetChatClient(deploymentName).AsIChatClient());
    }
}
