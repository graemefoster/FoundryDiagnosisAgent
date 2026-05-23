using Azure.AI.AgentServer.Core;
using Azure.AI.AgentServer.Invocations;
using Azure.Identity;
using CopilotAgent;

var builder = AgentHost.CreateBuilder(args);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(
                builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()));

builder.Services.AddSingleton<DefaultAzureCredential>();
builder.Services.Configure<CopilotHostedAgentOptions>(
    builder.Configuration.GetSection(CopilotHostedAgentOptions.SectionName));
builder.Services.AddSingleton<CopilotSessionManager>();

builder.Services.AddInvocationsServer();
builder.Services.AddScoped<InvocationHandler, GitHubCopilotInvocationHandler>();

builder.RegisterProtocol("invocations", endpoints => endpoints.MapInvocationsServer());

var app = builder.Build();
app.App.UseCors();
await app.RunAsync();
