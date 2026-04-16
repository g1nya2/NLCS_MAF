# 01. 단일 문서번역 에이전트 만들기

이 실습의 목표는 **Program.cs 한 파일 안에서** 다음 흐름을 완성하는 것입니다.

- Azure OpenAI 기반 채팅 클라이언트 연결
- Azure Blob Storage 연결
- Azure Document Translation 연결
- 단일 에이전트 등록
- PDF 업로드 API
- 5-pass 문서번역 시작 API
- 상태 조회 API
- 결과 조회 API

중요한 점은, 이 실습에서는 **UI/스타일 코드나 반복적인 보일러플레이트는 기본 제공**됩니다.
참가자는 아래 단계에 따라 **핵심 블록만 Program.cs에 채워 넣어** 최종 파일을 완성합니다.

---

## 사전 준비

Azure Portal에서 아래 리소스를 먼저 준비합니다.

1. **Azure OpenAI 리소스**
   - Endpoint
   - API Key
   - Deployment Name

2. **Azure AI Translator 리소스**
   - Endpoint
   - API Key

3. **Azure Storage Account**
   - Connection String
   - 컨테이너 6개
     - `source`
     - `target-ko-1`
     - `target-en-2`
     - `target-ko-3`
     - `target-en-4`
     - `target-ko-5`

4. `appsettings.json`에 설정값 입력

---

## 실습 디렉토리 준비

리포지토리 루트를 먼저 설정합니다.

```bash
# zsh/bash
REPOSITORY_ROOT=$(git rev-parse --show-toplevel)
```

```powershell
# PowerShell
$REPOSITORY_ROOT = git rev-parse --show-toplevel
```

실습용 디렉토리를 준비합니다.

```bash
# zsh/bash
mkdir -p $REPOSITORY_ROOT/workshop && \
cp -a $REPOSITORY_ROOT/save-points/step-01/start/. $REPOSITORY_ROOT/workshop/
```

```powershell
# PowerShell
New-Item -Type Directory -Path $REPOSITORY_ROOT/workshop -Force && `
Copy-Item -Path $REPOSITORY_ROOT/save-points/step-01/start/* -Destination $REPOSITORY_ROOT/workshop -Recurse -Force
```

---

## 실습 방식

기본 제공된 `Program.cs` 템플릿을 엽니다.
템플릿 안에는 다음과 같은 주석이 들어 있습니다.

- `// [STEP 1] ...`
- `// [STEP 2] ...`
- `// [STEP 3] ...`

각 단계에서는 **해당 주석 위치를 찾은 뒤**, 아래 코드 블록을 그대로 넣으면 됩니다.

---

## STEP 1. LLM Chat Client 생성 및 등록

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 1] LLM chat client를 생성하고 DI에 등록하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
IChatClient chatClient = await ChatClientFactory.CreateChatClientAsync(builder.Configuration, args);
builder.Services.AddChatClient(chatClient);
```

### 이 단계의 의미

이 코드는 에이전트가 사용할 LLM 연결을 준비합니다.
이번 실습에서는 `appsettings.json`의 `LlmProvider` 값을 기준으로 Azure OpenAI, Ollama, GitHub Models 중 하나를 선택할 수 있게 설계합니다.

---

## STEP 2. Document Translation Client 등록

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 2] DocumentTranslationClient를 DI에 등록하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = config["Azure:Translator:Endpoint"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Translator:Endpoint");
    var apiKey = config["Azure:Translator:ApiKey"]
        ?? throw new InvalidOperationException("Missing configuration: Azure:Translator:ApiKey");

    return new DocumentTranslationClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
});
```

### 이 단계의 의미

이 코드는 실제 문서번역 작업을 수행하는 Azure Document Translation SDK 클라이언트를 등록합니다.
즉, **번역 자체는 이 클라이언트가 담당**하고, 에이전트는 그 흐름을 조율합니다.

---

## STEP 3. 단일 에이전트 등록

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 3] 문서번역 오케스트레이션용 단일 에이전트를 등록하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
builder.AddAIAgent(
    name: "document-translation-5pass-agent",
    instructions: """
You are a single agent that orchestrates a 5-pass Azure Document Translation workflow.
You do not perform translation directly.
You manage upload, translation workflow, progress reporting, and result reporting.
Always answer in Korean unless explicitly asked otherwise.
""");
```

### 이 단계의 의미

이 에이전트는 번역을 직접 하지 않습니다.
대신 업로드, 진행 상태, 5-pass 실행 흐름, 최종 결과를 관리하는 **단일 오케스트레이터 에이전트** 역할을 합니다.

---

## STEP 4. 5-pass 시작 API 구현

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 4] 5-pass 번역 시작 API를 구현하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
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
            job.CurrentPass = 1;
            job.Step = "pass-1";
            job.Message = "1차 번역 시작: source -> target-ko-1";
            job.Pass1Status = "running";
            job.Pass1OperationId = await RunTranslationPassAsync(translationClient, sourceContainer, request.BlobName, targetKo1, "ko");
            job.Pass1Status = "completed";

            job.CurrentPass = 2;
            job.Step = "pass-2";
            job.Message = "2차 번역 시작: target-ko-1 -> target-en-2";
            job.Pass2Status = "running";
            job.Pass2OperationId = await RunTranslationPassAsync(translationClient, targetKo1, request.BlobName, targetEn2, "en");
            job.Pass2Status = "completed";

            job.CurrentPass = 3;
            job.Step = "pass-3";
            job.Message = "3차 번역 시작: target-en-2 -> target-ko-3";
            job.Pass3Status = "running";
            job.Pass3OperationId = await RunTranslationPassAsync(translationClient, targetEn2, request.BlobName, targetKo3, "ko");
            job.Pass3Status = "completed";

            job.CurrentPass = 4;
            job.Step = "pass-4";
            job.Message = "4차 번역 시작: target-ko-3 -> target-en-4";
            job.Pass4Status = "running";
            job.Pass4OperationId = await RunTranslationPassAsync(translationClient, targetKo3, request.BlobName, targetEn4, "en");
            job.Pass4Status = "completed";

            job.CurrentPass = 5;
            job.Step = "pass-5";
            job.Message = "5차 번역 시작: target-en-4 -> target-ko-5";
            job.Pass5Status = "running";
            job.Pass5OperationId = await RunTranslationPassAsync(translationClient, targetEn4, request.BlobName, targetKo5, "ko");
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

    return Results.Json(new { jobId, status = "running" });
});
```

### 이 단계의 의미

이 단계가 실습의 핵심입니다.
하나의 업로드된 PDF에 대해 번역을 **5번 연속 수행**하는 작업 흐름을 백그라운드에서 실행합니다.

---

## STEP 5. 상태 조회 API 구현

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 5] 번역 상태 조회 API를 구현하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
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
```

### 이 단계의 의미

에이전트가 관리 중인 번역 작업이 지금 어느 단계인지 외부에서 조회할 수 있게 합니다.

---

## STEP 6. 결과 조회 API 구현

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 6] 최종 결과 조회 API를 구현하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
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
```

### 이 단계의 의미

5-pass가 모두 끝난 뒤, 참가자는 최종 결과 blob의 SAS URL을 받아 결과 파일을 확인할 수 있습니다.

---

## STEP 7. 단일 Pass 실행 헬퍼 구현

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 7] 단일 번역 pass를 실행하는 헬퍼를 구현하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
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
```

### 이 단계의 의미

이 함수는 **문서 1회 번역**을 담당합니다.
5-pass 흐름은 결국 이 함수를 서로 다른 source/target container와 언어 조합으로 5번 호출하는 구조입니다.

---

## STEP 8. ChatClientFactory 분기 구현

`Program.cs`에서 아래 주석을 찾으세요.

```csharp
// [STEP 8] provider 이름에 따라 적절한 chat client를 반환하세요.
```

여기에 아래 코드를 넣으세요.

```csharp
IChatClient chatClient = normalizedProvider switch
{
    "Ollama" => await CreateOllamaChatClientAsync(config, normalizedProvider),
    "GitHubModels" => await CreateGitHubModelsChatClientAsync(config, normalizedProvider),
    "AzureOpenAI" => await CreateAzureOpenAIChatClientAsync(config, normalizedProvider),
    _ => throw new NotSupportedException($"The specified LLM provider '{provider}' is not supported.")
};
```

### 이 단계의 의미

워크숍에서 참가자가 다양한 LLM 공급자를 실험할 수 있도록 분기 구조를 완성합니다.
이번 실습의 기본값은 `AzureOpenAI`입니다.

---

## 실행

```bash
dotnet restore
dotnet build
dotnet watch run
```

브라우저에서 앱을 실행한 뒤 PDF를 업로드하고, 아래 흐름을 확인합니다.

1. 업로드 성공
2. 5-pass 시작
3. 상태 조회
4. 결과 조회

---

## 마무리

이 실습을 완료하면 참가자는 다음을 이해하게 됩니다.

- 단일 에이전트가 직접 번역하는 것이 아니라 **번역 워크플로를 오케스트레이션**한다는 점
- Blob Storage와 Translator를 조합해 다단계 문서번역 파이프라인을 만들 수 있다는 점
- MAF 기반 앱에서 핵심 로직만 단계별로 채워 넣는 방식으로 실습 문서를 설계할 수 있다는 점
