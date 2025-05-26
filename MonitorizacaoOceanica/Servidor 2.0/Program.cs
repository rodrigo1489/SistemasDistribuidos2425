using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using ClosedXML.Excel;
using System.Collections.Generic;
using Grpc.Net.Client;
using Analyze;            // gRPC Analysis service namespace
using Preprocess;         // gRPC Preprocess service namespace
using Microsoft.EntityFrameworkCore;
using Servidor20.Data;    // EF Core DbContext namespace
using Servidor20.Models;  // EF Core entity namespace

namespace Servidor20
{
    class Servidor
    {
        static string connString =
          "Server=localhost,1433;" +
          "Database=MonitorizacaoOceanica;" +
          "Trusted_Connection=True;" +
          "Encrypt=False;" +
          "TrustServerCertificate=True;";

        // gRPC channels & clients
        private static readonly GrpcChannel _analysisChannel =
            GrpcChannel.ForAddress("http://localhost:5002");
        private static readonly AnalysisService.AnalysisServiceClient
            _analysisClient = new(_analysisChannel);

        // Excel lock and path
        static object fileLock = new object();
        static string excelFilePath = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\dados_servidor.xlsx";

        // Mutexes per agregador for thread-safe file writes
        static Dictionary<string, Mutex> ficheiroMutexes = new();
        static object mutexLock = new();

        static void Main()
        {
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                LimparFicheirosDeRegisto();
                ApagarConteudoAgregadorData();
                Environment.Exit(0);
            };

            var listener = new TcpListener(IPAddress.Any, 9000);
            listener.Start();
            Console.WriteLine("[SERVIDOR] À escuta na porta 9000...");

            // console command thread
            new Thread(() =>
            {
                while (true)
                {
                    var cmd = Console.ReadLine();
                    if (cmd?.ToLower() == "desligar servidor")
                    {
                        LimparFicheirosDeRegisto();
                        ApagarConteudoAgregadorData();
                        Environment.Exit(0);
                    }
                }
            }).Start();

            while (true)
            {
                var client = listener.AcceptTcpClient();
                new Thread(() => HandleClient(client)).Start();
            }
        }

        static Mutex ObterMutexParaAgregador(string aggId)
        {
            lock (mutexLock)
            {
                if (!ficheiroMutexes.ContainsKey(aggId))
                    ficheiroMutexes[aggId] = new Mutex();
                return ficheiroMutexes[aggId];
            }
        }

        static void GuardarNoExcel(Registo r)
        {
            lock (fileLock)
            {
                var wb = File.Exists(excelFilePath)
                    ? new XLWorkbook(excelFilePath)
                    : new XLWorkbook();
                var ws = wb.Worksheets.Contains("Registos")
                    ? wb.Worksheet("Registos")
                    : wb.Worksheets.Add("Registos");

                if (ws.LastRowUsed() == null)
                {
                    var headers = new[]
                    {
                        "Id","TipoMensagem","AgregadorId","WavyId","TipoDado","Valor",
                        "Volume","Metodo","Timestamp","Origem","Destino","Media","DesvioPadrao"
                    };
                    for (int i = 0; i < headers.Length; i++)
                    {
                        ws.Cell(1, i + 1).Value = headers[i];
                        ws.Cell(1, i + 1).Style.Font.Bold = true;
                    }
                }

                int row = ws.LastRowUsed().RowNumber() + 1;
                ws.Cell(row, 1).Value = r.Id;
                ws.Cell(row, 2).Value = r.TipoMensagem;
                ws.Cell(row, 3).Value = r.AgregadorId;
                ws.Cell(row, 4).Value = r.WavyId;
                ws.Cell(row, 5).Value = r.TipoDado;
                ws.Cell(row, 6).Value = r.Valor;
                ws.Cell(row, 7).Value = r.Volume;
                ws.Cell(row, 8).Value = r.Metodo;
                ws.Cell(row, 9).Value = r.Timestamp.ToString("o");
                ws.Cell(row, 10).Value = r.Origem;
                ws.Cell(row, 11).Value = r.Destino;
                ws.Cell(row, 12).Value = r.Media;
                ws.Cell(row, 13).Value = r.DesvioPadrao;

                wb.SaveAs(excelFilePath);
            }
        }

        static void HandleClient(TcpClient client)
        {
            using var stream = client.GetStream();
            var buf = new byte[4096];
            int read = stream.Read(buf, 0, buf.Length);
            var message = Encoding.UTF8.GetString(buf, 0, read);
            Console.WriteLine("[SERVIDOR] Recebido:\n" + message);

            string response = "ERRO | SERVIDOR_01 | MENSAGEM_DESCONHECIDA";

            try
            {
                if (message.StartsWith("REGISTO") || message.StartsWith("DESLIGAR"))
                {
                    response = "CONFIRMADO | SERVIDOR_01 | RECEBIDO";
                }
                else if (message.StartsWith("DADOS"))
                {
                    var lines = message
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    // extract agregadorId from first line
                    var parts0 = lines[0].Split('|', StringSplitOptions.TrimEntries);
                    var aggId = parts0.Length >= 3 ? parts0[2] : "DESCONHECIDO";
                    var mutex = ObterMutexParaAgregador(aggId);
                    mutex.WaitOne();

                    var samples = new List<Preprocess.ProcessedSample>();
                    try
                    {
                        foreach (var line in lines)
                        {
                            var p = line.Split('|', StringSplitOptions.TrimEntries);
                            if (p.Length < 6) continue;

                            var tipoMsg = p[0];
                            var wavyId = p[1];
                            var agregIdMsg = p[2];
                            var tipoDado = p[3];
                            var valor = double.Parse(p[4]);
                            int? volume = int.TryParse(p[5], out var v) ? v : (int?)null;
                            var metodo = p.Length >= 7 ? p[6] : "";
                            string ts;
                            if (p.Length >= 8) ts = p[7];
                            else ts = DateTime.UtcNow.ToString("o");

                            var reg = new Registo
                            {
                                TipoMensagem = tipoMsg,
                                AgregadorId = agregIdMsg,
                                WavyId = wavyId,
                                TipoDado = tipoDado,
                                Valor = valor,
                                Volume = volume,
                                Metodo = metodo,
                                Timestamp = DateTime.Parse(ts),
                                Origem = wavyId,
                                Destino = agregIdMsg,
                                Media = null,
                                DesvioPadrao = null
                            };

                            // EF Core save
                            using (var db = new MonitoracaoContext())
                            {
                                db.Registos.Add(reg);
                                db.SaveChanges();
                            }

                            // Excel save
                            GuardarNoExcel(reg);

                            // collect for analysis
                            samples.Add(new Preprocess.ProcessedSample
                            {
                                Origem = wavyId,
                                Tipo = tipoDado,
                                Valor = valor,
                                Timestamp = ts
                            });
                        }
                    }
                    finally
                    {
                        mutex.ReleaseMutex();
                    }

                    // call analysis RPC
                    try
                    {
                        var sensorTipo = samples.FirstOrDefault()?.Tipo ?? "Desconhecido";
                        var req = new AnalyzeRequest();
                        req.Samples.AddRange(samples);
                        var result = _analysisClient.Analyze(req);
                        Console.WriteLine($"[ANALYSIS] Média={result.Media:F2}, Desvio={result.Desviopadrao:F2}");

                        // persist analysis result
                        var anal = new Registo
                        {
                            TipoMensagem = "ANALISE",
                            AgregadorId = aggId,
                            WavyId = null,
                            TipoDado = sensorTipo,
                            Valor = null,
                            Volume = samples.Count,
                            Metodo = "rpc",
                            Timestamp = DateTime.UtcNow,
                            Origem = "ANALYSIS_SERVICE",
                            Destino = aggId,
                            Media = result.Media,
                            DesvioPadrao = result.Desviopadrao
                        };
                        using (var db = new MonitoracaoContext())
                        {
                            db.Registos.Add(anal);
                            db.SaveChanges();
                        }
                        GuardarNoExcel(anal);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ANALYSIS] Falha RPC Analyze ou ao gravar BD: {ex.Message}");
                        if (ex.InnerException != null)
                            Console.WriteLine($"[ANALYSIS]   InnerException: {ex.InnerException.Message}");
                    }

                    response = "CONFIRMADO | SERVIDOR_01 | RECEBIDO";
                }
                else if (message.StartsWith("COMANDO"))
                {
                    var p = message.Split('|', StringSplitOptions.TrimEntries);
                    if (p.Length >= 4 && p[3] == "desligar_servidor")
                    {
                        response = "CONFIRMADO | SERVIDOR_01 | DESLIGAR_SERVER_OK";
                        stream.Write(Encoding.UTF8.GetBytes(response));
                        LimparFicheirosDeRegisto();
                        ApagarConteudoAgregadorData();
                        Environment.Exit(0);
                    }
                    else
                    {
                        response = $"ERRO | SERVIDOR_01 | COMANDO_DESCONHECIDO";
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ANALYSIS] Falha RPC Analyze: {ex.Message}");
                if (ex.InnerException != null)
                    Console.WriteLine($"[ANALYSIS]   Inner: {ex.InnerException.Message}");
            }

            stream.Write(Encoding.UTF8.GetBytes(response));
            client.Close();
        }

        static void LimparFicheirosDeRegisto()
        {
            foreach (var f in new[]
            {
                @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregadores_config.txt",
                @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\wavys_config.txt",
                @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\estado_wavys.txt"
            })
            {
                if (File.Exists(f))
                {
                    File.WriteAllText(f, "");
                    Console.WriteLine($"[SERVIDOR] Ficheiro apagado: {f}");
                }
            }
        }

        static void ApagarConteudoAgregadorData()
        {
            var pasta = @"C:\Users\rodri\source\repos\SistemasDistribuidos2425.2\MonitorizacaoOceanica\agregador_data\";
            if (!Directory.Exists(pasta)) return;
            foreach (var f in Directory.GetFiles(pasta))
            {
                File.Delete(f);
                Console.WriteLine($"[SERVIDOR] Apagado: {f}");
            }
        }
    }
}
