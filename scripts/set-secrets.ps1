# Prompts for the API's secrets and stores them in .NET user-secrets (outside the repo).
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File scripts\set-secrets.ps1
# Press Enter at any prompt to leave that setting unchanged.
$project = Join-Path $PSScriptRoot "..\src\WclQuery.Api"

function Read-Secret([string]$prompt) {
    $secure = Read-Host -Prompt $prompt -AsSecureString
    [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
}

$values = [ordered]@{
    "WarcraftLogs:ClientId"     = Read-Host -Prompt "Warcraft Logs client ID"
    "WarcraftLogs:ClientSecret" = Read-Secret "Warcraft Logs client secret"
    "Claude:ApiKey"             = Read-Secret "Anthropic API key"
    "AzureOpenAI:Endpoint"      = Read-Host -Prompt "Azure OpenAI / Foundry endpoint (e.g. https://my-resource.openai.azure.com/)"
    "AzureOpenAI:Deployment"    = Read-Host -Prompt "Azure OpenAI deployment name (e.g. gpt-6.1-sol)"
    "AzureOpenAI:ApiKey"        = Read-Secret "Azure OpenAI API key"
    "Llm:Provider"              = Read-Host -Prompt "Which model to use: anthropic or azure-openai"
}

foreach ($key in $values.Keys) {
    if ([string]::IsNullOrWhiteSpace($values[$key])) { Write-Host "Unchanged: $key"; continue }
    dotnet user-secrets set $key $values[$key].Trim() --project $project | Out-Null
    Write-Host "Saved $key"
}
