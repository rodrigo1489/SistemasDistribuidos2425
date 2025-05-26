using PreprocessingService.Services;  // para PreprocessingServiceImpl

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
var app = builder.Build();

// Regista o teu serviço RPC
app.MapGrpcService<PreprocessingServiceImpl>();

// opcional: endpoint REST simples para testar
app.MapGet("/", () => "gRPC PreprocessingService a correr em http://localhost:5001");

app.Run("http://localhost:5001");
