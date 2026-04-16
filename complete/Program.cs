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

// LLM chat client
IChatClient chatClient = await ChatClientFactory.CreateChatClientAsync(builder.Configuration, args);
builder.Services.AddChatClient(chatClient);

// Blob client
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var connectionString = config["Azure:Storage:ConnectionString"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Storage:ConnectionString");

    return new BlobServiceClient(connectionString);
});

// Document Translation client
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["Azure:Translator:Endpoint"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Translator:Endpoint");
    var apiKey = config["Azure:Translator:ApiKey"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Translator:ApiKey");

    return new DocumentTranslationClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
});

// Single agent
builder.AddAIAgent(
    name: "document-translation-5pass-agent",
    instructions: """
You are a single agent that orchestrates a 5-pass Azure Document Translation workflow.
You do not perform translation directly.
You manage upload, translation workflow, progress reporting, and result reporting.
Always answer in Korean unless explicitly asked otherwise.
""");

// Optional OpenAI conversation history endpoints for dev UI
builder.Services.AddOpenAIResponses();
builder.Services.AddOpenAIConversations();

var app = builder.Build();

var jobs = new ConcurrentDictionary<string, Translation5PassJob>();

// Ensure containers exist on startup
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

app.MapGet("/", () => Results.Redirect("/translate-5pass"));

app.MapGet("/translate-5pass", () => Results.Content("""
<!DOCTYPE html>
<html lang="ko">
<head>
  <meta charset="utf-8" />
  <title>Azure Document Translation 5-Pass</title>
  <style>
    body {
      font-family: Arial, sans-serif;
      max-width: 960px;
      margin: 30px auto;
      line-height: 1.5;
      background: #f7f7fb;
      color: #222;
      padding: 0 16px;
    }
    h1 { margin-bottom: 8px; }
    .card {
      background: white;
      border: 1px solid #e6e6ef;
      border-radius: 16px;
      padding: 20px;
      box-shadow: 0 4px 20px rgba(0,0,0,0.04);
      margin-bottom: 18px;
    }
    label {
      display: block;
      margin-top: 12px;
      font-weight: 600;
    }
    input, button {
      margin-top: 6px;
      width: 100%;
      padding: 10px;
      box-sizing: border-box;
      font-size: 14px;
    }
    button {
      cursor: pointer;
      border: none;
      border-radius: 10px;
      background: #1f6feb;
      color: white;
      font-weight: 700;
      margin-top: 16px;
    }
    button:disabled {
      background: #93b7f3;
      cursor: not-allowed;
    }
    .chat {
      display: flex;
      flex-direction: column;
      gap: 12px;
      min-height: 220px;
    }
    .bubble {
      max-width: 88%;
      padding: 14px 16px;
      border-radius: 16px;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .user {
      align-self: flex-end;
      background: #dbeafe;
    }
    .assistant {
      align-self: flex-start;
      background: #111827;
      color: #f9fafb;
    }
    .meta {
      font-size: 12px;
      opacity: 0.75;
      margin-bottom: 6px;
    }
    .status {
      font-size: 13px;
      color: #555;
      margin-top: 8px;
      white-space: pre-wrap;
    }
  </style>
</head>
<body>
  <h1>Azure Document Translation 5-Pass</h1>
  <p>PDF를 업로드하면 Blob에 저장한 뒤, 5패스 문서 번역을 순차적으로 실행합니다.</p>

  <div class="card">
    <form id="form">
      <label>PDF 파일</label>
      <input type="file" name="file" accept=".pdf" required />
      <button id="submitBtn" type="submit">업로드 후 5패스 시작</button>
    </form>
  </div>

  <div class="card">
    <h2 style="margin-top:0;">진행 상태</h2>
    <div id="chat" class="chat"></div>
    <div id="status" class="status">아직 실행 전입니다.</div>
  </div>

  <script>
    const form = document.getElementById('form');
    const chat = document.getElementById('chat');
    const status = document.getElementById('status');
    const submitBtn = document.getElementById('submitBtn');

    function addBubble(role, text) {
      const wrapper = document.createElement('div');
      wrapper.className = 'bubble ' + role;

      const meta = document.createElement('div');
      meta.className = 'meta';
      meta.textContent = role === 'user' ? '사용자' : '에이전트';

      const body = document.createElement('div');
      body.textContent = text;

      wrapper.appendChild(meta);
      wrapper.appendChild(body);
      chat.appendChild(wrapper);
      chat.scrollTop = chat.scrollHeight;
      return body;
    }

    async function poll(jobId, liveBody) {
      while (true) {
        const res = await fetch(`/api/translate/status/${jobId}`);
        const data = await res.json();

        const text =
          `[상태] ${data.status}\n` +
          `[단계] ${data.step}\n` +
          `[메시지] ${data.message}\n` +
          `[현재 패스] ${data.currentPass}/${data.totalPasses}\n` +
          `[Pass1] ${data.pass1Status ?? '-'}\n` +
          `[Pass2] ${data.pass2Status ?? '-'}\n` +
          `[Pass3] ${data.pass3Status ?? '-'}\n` +
          `[Pass4] ${data.pass4Status ?? '-'}\n` +
          `[Pass5] ${data.pass5Status ?? '-'}`;

        status.textContent = text;
        liveBody.textContent = text;

        if (data.status === 'completed') {
          const resultRes = await fetch(`/api/translate/result/${jobId}`);
          const resultData = await resultRes.json();

          liveBody.textContent =
            text +
            `\n\n[최종 결과]\n컨테이너: ${resultData.finalContainer}\n파일: ${resultData.finalBlobName}\nURL: ${resultData.finalBlobUrl}`;
          submitBtn.disabled = false;
          return;
        }

        if (data.status === 'failed') {
          liveBody.textContent = text + `\n\n[오류]\n${data.error ?? '-'}`;
          submitBtn.disabled = false;
          return;
        }

        await new Promise(resolve => setTimeout(resolve, 5000));
      }
    }

    form.addEventListener('submit', async (e) => {
      e.preventDefault();
      submitBtn.disabled = true;

      const fileInput = form.querySelector('input[name="file"]');
      const file = fileInput.files[0];

      addBubble('user', `업로드 파일: ${file?.name ?? '파일 없음'}`);
      const liveBody = addBubble('assistant', '업로드 중입니다...');

      const formData = new FormData();
      formData.append('file', file);

      const uploadRes = await fetch('/api/translate/upload', {
        method: 'POST',
        body: formData
      });

      const uploadData = await uploadRes.json();

      if (!uploadRes.ok) {
        liveBody.textContent = `업로드 실패\n\n${uploadData.error ?? '알 수 없는 오류'}`;
        submitBtn.disabled = false;
        return;
      }

      liveBody.textContent = `업로드 완료\nblobName: ${uploadData.blobName}\n5패스를 시작합니다...`;

      const startRes = await fetch('/api/translate/start-5pass', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          blobName: uploadData.blobName,
          fileName: uploadData.fileName
        })
      });

      const startData = await startRes.json();

      if (!startRes.ok) {
        liveBody.textContent = `5패스 시작 실패\n\n${startData.error ?? '알 수 없는 오류'}`;
        submitBtn.disabled = false;
        return;
      }

      await poll(startData.jobId, liveBody);
    });
  </script>
</body>
</html>
""", "text/html; charset=utf-8"));

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

app.MapPost("/api/translate/start-5pass", async (
    StartFivePassRequest request,
    DocumentTranslationClient translationClient,
    BlobServiceClient blobServiceClient,
    IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(request.BlobName))
    {
        return Results.BadRequest(new { error = "blobName이 필요합니다." });
    }

    var sourceContainerName = config["Azure:Storage:SourceContainer"]!;
    var targetKo1Name = config["Azure:Storage:TargetKo1Container"]!;
    var targetEn2Name = config["Azure:Storage:TargetEn2Container"]!;
    var targetKo3Name = config["Azure:Storage:TargetKo3Container"]!;
    var targetEn4Name = config["Azure:Storage:TargetEn4Container"]!;
    var targetKo5Name = config["Azure:Storage:TargetKo5Container"]!;

    var sourceContainer = blobServiceClient.GetBlobContainerClient(sourceContainerName);
    var sourceBlob = sourceContainer.GetBlobClient(request.BlobName);

    if (!await sourceBlob.ExistsAsync())
    {
        return Results.BadRequest(new { error = "source 컨테이너에 해당 blob이 없습니다." });
    }

    var targetKo1 = blobServiceClient.GetBlobContainerClient(targetKo1Name);
    var targetEn2 = blobServiceClient.GetBlobContainerClient(targetEn2Name);
    var targetKo3 = blobServiceClient.GetBlobContainerClient(targetKo3Name);
    var targetEn4 = blobServiceClient.GetBlobContainerClient(targetEn4Name);
    var targetKo5 = blobServiceClient.GetBlobContainerClient(targetKo5Name);

    var jobId = Guid.NewGuid().ToString("N");

    var job = new Translation5PassJob
    {
        JobId = jobId,
        FileName = request.FileName ?? request.BlobName,
        BlobName = request.BlobName,
        Status = "running",
        Step = "pass-1",
        Message = "5패스 번역 작업을 시작합니다.",
        CurrentPass = 1,
        TotalPasses = 5,
        SourceContainer = sourceContainerName,
        FinalOutputContainer = targetKo5Name,
        CreatedAtUtc = DateTime.UtcNow
    };

    jobs[jobId] = job;

    _ = Task.Run(async () =>
    {
        try
        {
            // Pass 1: source -> ko1
            job.CurrentPass = 1;
            job.Step = "pass-1";
            job.Message = "1차 번역 시작: source -> target-ko-1";
            job.Pass1Status = "running";
            job.Pass1OperationId = await RunTranslationPassAsync(
                translationClient,
                sourceContainer,
                request.BlobName,
                targetKo1,
                "ko");
            job.Pass1Status = "completed";

            // Pass 2: ko1 -> en2
            job.CurrentPass = 2;
            job.Step = "pass-2";
            job.Message = "2차 번역 시작: target-ko-1 -> target-en-2";
            job.Pass2Status = "running";
            job.Pass2OperationId = await RunTranslationPassAsync(
                translationClient,
                targetKo1,
                request.BlobName,
                targetEn2,
                "en");
            job.Pass2Status = "completed";

            // Pass 3: en2 -> ko3
            job.CurrentPass = 3;
            job.Step = "pass-3";
            job.Message = "3차 번역 시작: target-en-2 -> target-ko-3";
            job.Pass3Status = "running";
            job.Pass3OperationId = await RunTranslationPassAsync(
                translationClient,
                targetEn2,
                request.BlobName,
                targetKo3,
                "ko");
            job.Pass3Status = "completed";

            // Pass 4: ko3 -> en4
            job.CurrentPass = 4;
            job.Step = "pass-4";
            job.Message = "4차 번역 시작: target-ko-3 -> target-en-4";
            job.Pass4Status = "running";
            job.Pass4OperationId = await RunTranslationPassAsync(
                translationClient,
                targetKo3,
                request.BlobName,
                targetEn4,
                "en");
            job.Pass4Status = "completed";

            // Pass 5: en4 -> ko5
            job.CurrentPass = 5;
            job.Step = "pass-5";
            job.Message = "5차 번역 시작: target-en-4 -> target-ko-5";
            job.Pass5Status = "running";
            job.Pass5OperationId = await RunTranslationPassAsync(
                translationClient,
                targetEn4,
                request.BlobName,
                targetKo5,
                "ko");
            job.Pass5Status = "completed";

            job.Status = "completed";
            job.Step = "completed";
            job.Message = "5패스 번역이 완료되었습니다.";
            job.CompletedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Message = "5패스 번역 중 오류가 발생했습니다.";
            job.Error = ex.ToString();
            job.CompletedAtUtc = DateTime.UtcNow;
        }
    });

    return Results.Json(new
    {
        jobId,
        status = "running"
    });
});

app.MapGet("/api/translate/status/{jobId}", (string jobId) =>
{
    if (!jobs.TryGetValue(jobId, out var job))
    {
        return Results.NotFound(new { error = "해당 작업을 찾을 수 없습니다." });
    }

    return Results.Json(new
    {
        job.JobId,
        job.FileName,
        job.Status,
        job.Step,
        job.Message,
        job.CurrentPass,
        job.TotalPasses,
        job.SourceContainer,
        job.FinalOutputContainer,
        job.Pass1OperationId,
        job.Pass2OperationId,
        job.Pass3OperationId,
        job.Pass4OperationId,
        job.Pass5OperationId,
        job.Pass1Status,
        job.Pass2Status,
        job.Pass3Status,
        job.Pass4Status,
        job.Pass5Status,
        job.Error,
        job.CreatedAtUtc,
        job.CompletedAtUtc
    });
});

app.MapGet("/api/translate/result/{jobId}", async (
    string jobId,
    BlobServiceClient blobServiceClient) =>
{
    if (!jobs.TryGetValue(jobId, out var job))
    {
        return Results.NotFound(new { error = "해당 작업을 찾을 수 없습니다." });
    }

    if (!string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "아직 완료되지 않았습니다." });
    }

    var finalContainer = blobServiceClient.GetBlobContainerClient(job.FinalOutputContainer);
    var finalBlob = finalContainer.GetBlobClient(job.BlobName);

    if (!await finalBlob.ExistsAsync())
    {
        return Results.NotFound(new { error = "최종 결과 blob을 찾을 수 없습니다." });
    }

    var finalBlobSasUri = GenerateBlobSasUri(finalBlob, TimeSpan.FromHours(24));

    return Results.Json(new
    {
        job.JobId,
        job.FileName,
        finalContainer = job.FinalOutputContainer,
        finalBlobName = job.BlobName,
        finalBlobUrl = finalBlobSasUri.ToString()
    });
});

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

static async Task<string> RunTranslationPassAsync(
    DocumentTranslationClient client,
    BlobContainerClient sourceContainer,
    string sourceBlobName,
    BlobContainerClient targetContainer,
    string targetLanguage)
{
    var sourceBlob = sourceContainer.GetBlobClient(sourceBlobName);

    if (!await sourceBlob.ExistsAsync())
    {
        throw new InvalidOperationException(
            $"입력 blob이 없습니다: {sourceContainer.Name}/{sourceBlobName}");
    }

    var sourceContainerSas = GenerateSourceContainerSasUri(sourceContainer, TimeSpan.FromHours(24));
    var targetContainerSas = GenerateTargetContainerSasUri(targetContainer, TimeSpan.FromHours(24));

    var input = new DocumentTranslationInput(sourceContainerSas, targetContainerSas, targetLanguage);
    input.Source.Prefix = sourceBlobName;

    DocumentTranslationOperation operation = await client.StartTranslationAsync(input);
    await operation.WaitForCompletionAsync();

    if (operation.DocumentsFailed > 0)
    {
        throw new InvalidOperationException(
            $"번역 작업 실패. OperationId={operation.Id}, Succeeded={operation.DocumentsSucceeded}, Failed={operation.DocumentsFailed}");
    }

    return operation.Id;
}

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

        IChatClient chatClient = normalizedProvider switch
        {
            "Ollama" => await CreateOllamaChatClientAsync(config, normalizedProvider),
            "GitHubModels" => await CreateGitHubModelsChatClientAsync(config, normalizedProvider),
            "AzureOpenAI" => await CreateAzureOpenAIChatClientAsync(config, normalizedProvider),
            _ => throw new NotSupportedException($"The specified LLM provider '{provider}' is not supported.")
        };

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
